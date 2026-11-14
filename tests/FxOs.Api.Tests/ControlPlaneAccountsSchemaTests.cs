using System.Net;
using System.Security.Cryptography;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Control-plane V0002: identities, memberships, invitations, tokens, sessions, outbox and audit log.</summary>
[Collection(PlatformCollection.Name)]
public sealed class ControlPlaneAccountsSchemaTests(PlatformFixture fx)
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task Control_plane_has_the_account_tables()
    {
        await using var conn = await OpenAsync();
        var tables = (await conn.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public'")).ToList();

        foreach (var table in new[]
                 {
                     "user_account", "tenant_membership", "invitation", "account_token", "session",
                     "email_outbox", "control_audit_log",
                 })
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task Emails_are_unique_and_must_be_stored_lower_case()
    {
        await using var conn = await OpenAsync();
        var email = NewEmail();
        await CreateUserAsync(conn, email);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => CreateUserAsync(conn, email));
        Assert.Equal(UniqueViolation, duplicate.SqlState);

        var mixedCase = await Assert.ThrowsAsync<PostgresException>(() => CreateUserAsync(conn, "Ana." + NewEmail()));
        Assert.Equal(CheckViolation, mixedCase.SqlState);
    }

    [Fact]
    public async Task Only_argon2id_hashes_are_accepted()
    {
        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "insert into user_account (email, display_name, password_hash, password_changed_at) values (@e, 'Ana', 'plain-text', now())",
            new { e = NewEmail() }));
        Assert.Equal(CheckViolation, ex.SqlState);

        await conn.ExecuteAsync(
            "insert into user_account (email, display_name, password_hash, password_changed_at) values (@e, 'Ana', '$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA', now())",
            new { e = NewEmail() });
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("approver", true)]
    [InlineData("standard", true)]
    [InlineData("owner", false)]
    [InlineData("viewer", false)]
    public async Task Memberships_take_only_the_three_roles(string role, bool allowed)
    {
        var tenant = await fx.ProvisionAsync("role");
        await using var conn = await OpenAsync();
        var user = await CreateUserAsync(conn, NewEmail());

        var insert = () => conn.ExecuteAsync(
            "insert into tenant_membership (tenant_id, user_id, role) values (@t, @u, @role)",
            new { t = tenant.Id, u = user, role });

        if (allowed)
        {
            await insert();
        }
        else
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(insert);
            Assert.Equal(CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task One_open_invitation_per_tenant_and_email_and_a_revoked_one_can_be_replaced()
    {
        var tenant = await fx.ProvisionAsync("invite");
        await using var conn = await OpenAsync();
        var email = NewEmail();

        var first = await InviteAsync(conn, tenant.Id, email);
        var second = await Assert.ThrowsAsync<PostgresException>(() => InviteAsync(conn, tenant.Id, email));
        Assert.Equal(UniqueViolation, second.SqlState);

        // Resend: revoke the open link, then issue a new one.
        await conn.ExecuteAsync("update invitation set revoked_at = now() where id = @first", new { first });
        await InviteAsync(conn, tenant.Id, email);
    }

    [Fact]
    public async Task Setup_invitations_are_for_admins_only()
    {
        var tenant = await fx.ProvisionAsync("setup");
        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InviteAsync(conn, tenant.Id, NewEmail(), kind: "tenant_setup", role: "standard"));
        Assert.Equal(CheckViolation, ex.SqlState);

        await InviteAsync(conn, tenant.Id, NewEmail(), kind: "tenant_setup", role: "admin");
    }

    [Fact]
    public async Task Token_hashes_must_be_sha256_sized()
    {
        var tenant = await fx.ProvisionAsync("hash");
        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InviteAsync(conn, tenant.Id, NewEmail(), tokenHash: RandomNumberGenerator.GetBytes(16)));
        Assert.Equal(CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Email_change_tokens_carry_the_new_address_and_resets_do_not()
    {
        await using var conn = await OpenAsync();
        var user = await CreateUserAsync(conn, NewEmail());

        const string Insert = """
            insert into account_token (user_id, purpose, token_hash, new_email, expires_at)
            values (@user, @purpose, @hash, @newEmail, now() + interval '1 hour')
            """;

        var missing = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(Insert,
            new { user, purpose = "email_change", hash = Hash(), newEmail = (string?)null }));
        Assert.Equal(CheckViolation, missing.SqlState);

        await conn.ExecuteAsync(Insert, new { user, purpose = "email_change", hash = Hash(), newEmail = NewEmail() });
        await conn.ExecuteAsync(Insert, new { user, purpose = "password_reset", hash = Hash(), newEmail = (string?)null });

        var secondReset = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(Insert,
            new { user, purpose = "password_reset", hash = Hash(), newEmail = (string?)null }));
        Assert.Equal(UniqueViolation, secondReset.SqlState);
    }

    [Fact]
    public async Task Control_audit_log_is_append_only()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            "insert into control_audit_log (actor_kind, action, detail) values ('system', 'test.appended', '{}')");

        foreach (var sql in new[]
                 {
                     "update control_audit_log set action = 'test.changed'",
                     "delete from control_audit_log",
                     "truncate control_audit_log",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
            Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Archived_tenants_are_refused_by_tenant_routes()
    {
        var tenant = await fx.ProvisionAsync("archive");
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update tenant set status = @archived, archived_at = now() where id = @id",
                new { archived = TenantStatus.Archived, id = tenant.Id });

            // Status and archived_at move together.
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
                "update tenant set archived_at = null where id = @id", new { id = tenant.Id }));
            Assert.Equal(CheckViolation, ex.SqlState);
        }

        var response = await fx.Client().SendAsync(
            PlatformFixture.Request(HttpMethod.Get, "/api/tenant/audit", tenant.Slug));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();

    private static string NewEmail() => $"user_{Guid.NewGuid():N}@example.test";

    private static byte[] Hash() => SHA256.HashData(RandomNumberGenerator.GetBytes(32));

    private static Task<Guid> CreateUserAsync(NpgsqlConnection conn, string email) =>
        conn.ExecuteScalarAsync<Guid>(
            "insert into user_account (email, display_name) values (@email, 'Test user') returning id", new { email });

    private static Task<Guid> InviteAsync(
        NpgsqlConnection conn, Guid tenantId, string email, string kind = "teammate", string role = "standard",
        byte[]? tokenHash = null) =>
        conn.ExecuteScalarAsync<Guid>(
            """
            insert into invitation (tenant_id, kind, email, display_name, role, token_hash, expires_at)
            values (@tenantId, @kind, @email, 'Invitee', @role, @hash, now() + interval '7 days')
            returning id
            """,
            new { tenantId, kind, email, role, hash = tokenHash ?? Hash() });
}
