using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Sprint 2 step 2: sign-in (T-06), lock (T-S2), sign-out and the current session.</summary>
[Collection(PlatformCollection.Name)]
public sealed class SignInTests(PlatformFixture fx)
{
    // Older Argon2id parameters (m=65536, t=3, p=4) for "password", from OpenSSL: must still sign in, then be rehashed.
    private const string OlderParametersHash =
        "$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHRzb21lc2FsdA$gduXp+Z6iReEolmbyHn5V8s1EtJzmEvZfYoY/Fn/AeI";

    [Fact]
    public async Task The_right_password_opens_a_session_in_the_company_and_is_audited()
    {
        var tenant = await fx.ProvisionAsync("signin");
        var (userId, email, password) = await MemberAsync(tenant);

        var response = await SignInAsync(email.ToUpperInvariant(), password, remember: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(tenant.Slug, body.GetProperty("session").GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal("admin", body.GetProperty("session").GetProperty("role").GetString());

        var client = fx.Client();
        var session = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token))).StatusCode);

        var audit = await (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/audit", token)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(audit.EnumerateArray(), e =>
            e.GetProperty("action").GetString() == "auth.signed_in" && e.GetProperty("actor").GetString() == $"user:{userId:D}");

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>(
            "select count(*) from control_audit_log where action = 'account.signed_in' and actor_user_id = @userId", new { userId }));
    }

    [Fact]
    public async Task Wrong_passwords_and_unknown_emails_get_the_same_answer()
    {
        var tenant = await fx.ProvisionAsync("wrong");
        var (_, email, _) = await MemberAsync(tenant);

        var wrong = await SignInAsync(email, "Not-the-password-1!");
        var unknown = await SignInAsync("nobody_" + email, "Not-the-password-1!");
        var empty = await SignInAsync("", "");

        foreach (var response in new[] { wrong, unknown, empty })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("""{"code":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_sign_in_even_with_the_right_one()
    {
        var tenant = await fx.ProvisionAsync("lock");
        var (_, email, password) = await MemberAsync(tenant);

        for (var i = 1; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(email, "Wrong-password-1!")).StatusCode);
        }

        var locked = await SignInAsync(email, "Wrong-password-1!");
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<JsonElement>();
        var until = body.GetProperty("lockedUntil").GetDateTimeOffset();
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.5));

        Assert.Equal(HttpStatusCode.Locked, (await SignInAsync(email, password)).StatusCode);
    }

    [Fact]
    public async Task Deactivated_members_cannot_sign_in()
    {
        var tenant = await fx.ProvisionAsync("deact");
        var (userId, email, password) = await MemberAsync(tenant);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update tenant_membership set status = 'deactivated', deactivated_at = now() where user_id = @userId", new { userId });
        }

        var response = await SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"code":"no_access"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_hash_with_older_parameters_is_replaced_on_sign_in()
    {
        var tenant = await fx.ProvisionAsync("rehash");
        var (userId, email, _) = await MemberAsync(tenant, hash: OlderParametersHash);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(email, "password")).StatusCode);

        await using var conn = await OpenAsync();
        var stored = await conn.ExecuteScalarAsync<string>("select password_hash from user_account where id = @userId", new { userId });
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 7 * 24)]
    [InlineData(false, 12)]
    public async Task Keep_me_signed_in_chooses_the_session_length(bool remember, int hours)
    {
        var tenant = await fx.ProvisionAsync("length");
        var (_, email, password) = await MemberAsync(tenant);

        var body = await (await SignInAsync(email, password, remember)).Content.ReadFromJsonAsync<JsonElement>();
        var expires = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromHours(hours) - TimeSpan.FromMinutes(2), TimeSpan.FromHours(hours));
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var tenant = await fx.ProvisionAsync("signout");
        var (_, email, password) = await MemberAsync(tenant);
        var token = (await (await SignInAsync(email, password)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var client = fx.Client();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/auth/sign-out", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token))).StatusCode);

        // Twice is fine, and so is no session at all.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/auth/sign-out", token))).StatusCode);
    }

    [Fact]
    public async Task Sign_in_lands_in_the_company_used_last()
    {
        var first = await fx.ProvisionAsync("multi");
        var second = await fx.ProvisionAsync("multi");
        var (userId, email, password) = await MemberAsync(first);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "insert into tenant_membership (tenant_id, user_id, role) values (@t, @u, 'standard')", new { t = second.Id, u = userId });
            await conn.ExecuteAsync(
                "insert into session (user_id, tenant_id, token_hash, expires_at, revoked_at, revoked_reason) values (@u, @t, @h, now() + interval '1 hour', now(), 'sign_out')",
                new { u = userId, t = second.Id, h = SecureToken.New().Hash });
        }

        var body = await (await SignInAsync(email, password)).Content.ReadFromJsonAsync<JsonElement>();
        var session = body.GetProperty("session");
        Assert.Equal(second.Slug, session.GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal("standard", session.GetProperty("role").GetString());
    }

    private Task<HttpResponseMessage> SignInAsync(string email, string password, bool remember = false) =>
        fx.Client().PostAsJsonAsync("/api/auth/sign-in", new { email, password, remember });

    private async Task<(Guid UserId, string Email, string Password)> MemberAsync(TenantRecord tenant, string? hash = null)
    {
        var password = $"Pw-{Guid.NewGuid():N}1!";
        hash ??= fx.Services.GetRequiredService<IPasswordHasher>().Hash(password);
        var (userId, email, _) = await fx.SignInAsync(tenant, "admin", hash);
        return (userId, email, password);
    }

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();
}
