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

/// <summary>Sprint 3: tenant admin portal API, platform-admin sessions, setup steps and tenant menu.</summary>
[Collection(PlatformCollection.Name)]
public sealed class TenantAdminTests(PlatformFixture fx)
{
    [Fact]
    public async Task Only_platform_admins_reach_the_portal_api()
    {
        var client = fx.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/tenants")).StatusCode);

        var tenant = await fx.ProvisionAsync("notadm");
        var (_, _, memberToken) = await fx.SignInAsync(tenant, "admin");
        var forbidden = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/admin/tenants", memberToken));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (_, admin) = await PlatformAdminAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/admin/tenants", admin))).StatusCode);
    }

    [Fact]
    public async Task A_platform_admin_without_a_company_signs_in_to_the_portal_and_opens_dummy_tenants_only()
    {
        var dummy = await fx.ProvisionAsync("padum");
        var slug = $"real_{Guid.NewGuid():N}"[..14];
        var real = await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Real Co", isDummy: false);
        var (_, token) = await PlatformAdminAsync();
        var client = fx.Client();

        var session = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token)));
        Assert.Equal(JsonValueKind.Null, session.GetProperty("tenant").ValueKind);
        Assert.True(session.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token))).StatusCode);

        var companies = (await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/companies", token))))
            .EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToList();
        Assert.Contains(dummy.Slug, companies);
        Assert.DoesNotContain(real.Slug, companies);

        var toReal = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/auth/session/company", token, new { tenantId = real.Id }));
        Assert.Equal(HttpStatusCode.Forbidden, toReal.StatusCode);

        var toDummy = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/auth/session/company", token, new { tenantId = dummy.Id }));
        Assert.Equal(HttpStatusCode.OK, toDummy.StatusCode);
        var profile = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token)));
        Assert.Equal(dummy.Slug, profile.GetProperty("slug").GetString());
        var now = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token)));
        Assert.Equal("admin", now.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Members_switch_between_their_own_companies()
    {
        var first = await fx.ProvisionAsync("mswa");
        var second = await fx.ProvisionAsync("mswb");
        var other = await fx.ProvisionAsync("mswc");
        var (userId, _, token) = await fx.SignInAsync(first, "standard");
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync("insert into tenant_membership (tenant_id, user_id, role) values (@t, @u, 'approver')",
                new { t = second.Id, u = userId });
        }

        var client = fx.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Post, "/api/auth/session/company", token, new { tenantId = second.Id }))).StatusCode);
        var session = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token)));
        Assert.Equal((second.Slug, "approver"),
            (session.GetProperty("tenant").GetProperty("slug").GetString(), session.GetProperty("role").GetString()));

        // Not a member, not a platform admin: refused, even for a dummy tenant.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Post, "/api/auth/session/company", token, new { tenantId = other.Id }))).StatusCode);
    }

    [Fact]
    public async Task Creating_a_tenant_runs_setup_and_invites_the_first_admin()
    {
        var (adminId, token) = await PlatformAdminAsync();
        var client = fx.Client();
        var email = $"first_{Guid.NewGuid():N}@example.test";
        var name = $"Setup Co {Guid.NewGuid():N}"[..17];

        var created = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/admin/tenants", token,
            new { name, firstAdminEmail = email.ToUpperInvariant(), realData = false }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var tenantId = (await Json(created)).GetProperty("id").GetGuid();

        var runner = fx.Services.GetRequiredService<TenantSetupRunner>();
        Assert.True(await runner.RunOnceAsync() >= 1);
        var afterDb = await TenantAsync(client, token, tenantId);
        Assert.Equal("provisioning", afterDb.GetProperty("status").GetString());
        Assert.Equal(("rate_history", "pending"), (Step(afterDb), Status(afterDb)));

        // Not in anyone's tenant menu until setup finishes.
        var companies = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/companies", token)));
        Assert.DoesNotContain(companies.EnumerateArray(), c => c.GetProperty("id").GetGuid() == tenantId);

        // The Python worker copies the rate history and hands over to the invitation step.
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync("update tenant_job set step = 'invitation', status = 'pending' where tenant_id = @tenantId",
                new { tenantId });
        }

        await runner.RunOnceAsync();
        var done = await TenantAsync(client, token, tenantId);
        Assert.Equal("active", done.GetProperty("status").GetString());
        Assert.Equal(("done", "done"), (Step(done), Status(done)));
        Assert.Equal(2, done.GetProperty("setup").GetProperty("events").GetArrayLength());

        await using var check = await OpenAsync();
        var invite = await check.QuerySingleAsync<(string Kind, string Role, string Email)>(
            "select kind, role, email from invitation where tenant_id = @tenantId", new { tenantId });
        Assert.Equal(("tenant_setup", "admin", email), invite);
        Assert.Equal("tenant_setup_invitation", await check.ExecuteScalarAsync<string>(
            "select template from email_outbox where to_email = @email", new { email }));
        Assert.Equal(1L, await check.ExecuteScalarAsync<long>(
            "select count(*) from control_audit_log where action = 'tenant.created' and actor_user_id = @adminId and tenant_id = @tenantId",
            new { adminId, tenantId }));
    }

    [Fact]
    public async Task A_failed_step_is_retried_from_where_it_stopped()
    {
        var (_, token) = await PlatformAdminAsync();
        var client = fx.Client();
        var created = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/admin/tenants", token,
            new { name = "Retry Co", firstAdminEmail = $"r_{Guid.NewGuid():N}@example.test", realData = false }));
        var tenantId = (await Json(created)).GetProperty("id").GetGuid();
        await fx.Services.GetRequiredService<TenantSetupRunner>().RunOnceAsync();
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update tenant_job set status = 'failed', error = 'raw store did not respond', progress = 68 where tenant_id = @tenantId",
                new { tenantId });
        }

        var failed = await TenantAsync(client, token, tenantId);
        Assert.Equal(("rate_history", "failed"), (Step(failed), Status(failed)));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/retry", token, new { }))).StatusCode);
        var retried = await TenantAsync(client, token, tenantId);
        Assert.Equal(("rate_history", "pending"), (Step(retried), Status(retried)));
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/retry", token, new { }))).StatusCode);
    }

    [Fact]
    public async Task Rename_archive_and_restore()
    {
        var tenant = await fx.ProvisionAsync("arch");
        var (_, memberToken) = (0, (await fx.SignInAsync(tenant)).Token);
        var (_, token) = await PlatformAdminAsync();
        var client = fx.Client();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{tenant.Id}/rename", token, new { name = "Renamed Co" }))).StatusCode);
        var profile = await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", memberToken)));
        Assert.Equal("Renamed Co", profile.GetProperty("displayName").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{tenant.Id}/archive", token, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", memberToken))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{tenant.Id}/restore", token, new { }))).StatusCode);
        Assert.Equal("active", (await TenantAsync(client, token, tenant.Id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_needs_the_exact_name_and_never_touches_real_data()
    {
        var (_, token) = await PlatformAdminAsync();
        var client = fx.Client();
        var slug = $"real_{Guid.NewGuid():N}"[..14];
        var real = await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Real Co", isDummy: false);
        var refused = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, $"/api/admin/tenants/{real.Id}/delete", token,
            new { confirmName = "Real Co" }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var dummy = await fx.ProvisionAsync("delme");
        var (memberId, _, memberToken) = await fx.SignInAsync(dummy);
        var mismatch = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, $"/api/admin/tenants/{dummy.Id}/delete", token,
            new { confirmName = "wrong" }));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var deleted = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, $"/api/admin/tenants/{dummy.Id}/delete", token,
            new { confirmName = dummy.DisplayName }));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", memberToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, $"/api/admin/tenants/{dummy.Id}", token))).StatusCode);

        await using var conn = await OpenAsync();
        Assert.Equal("deleted", await conn.ExecuteScalarAsync<string>("select status from tenant where id = @id", new { id = dummy.Id }));
        Assert.Equal("deactivated", await conn.ExecuteScalarAsync<string>(
            "select status from tenant_membership where user_id = @memberId", new { memberId }));
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "select exists (select 1 from pg_database where datname = @db)", new { db = dummy.DatabaseName }));
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "select exists (select 1 from dev_secret where secret_ref = @s)", new { s = dummy.SecretRef }));
    }

    [Fact]
    public async Task Dummy_data_jobs_queue_once_and_only_for_dummy_tenants()
    {
        var (_, token) = await PlatformAdminAsync();
        var client = fx.Client();
        var dummy = await fx.ProvisionAsync("dumq");
        var slug = $"real_{Guid.NewGuid():N}"[..14];
        var real = await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Real Co", isDummy: false);

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{real.Id}/dummy-data", token, new { load = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{dummy.Id}/dummy-data", token, new { load = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post,
            $"/api/admin/tenants/{dummy.Id}/dummy-data", token, new { load = false }))).StatusCode);

        var view = await TenantAsync(client, token, dummy.Id);
        Assert.Equal("load_dummy", view.GetProperty("dummyJob").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_platform_admin_with_no_company_can_reset_their_password()
    {
        var (_, _) = await PlatformAdminAsync();
        var email = _lastEmail!;
        var forgot = await fx.Client().PostAsJsonAsync("/api/auth/password/forgot", new { email, locale = "en-US" });
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>(
            "select count(*) from email_outbox where to_email = @email and template = 'password_reset'", new { email }));
    }

    [Theory]
    [InlineData("Arkus Nexus", "arkus_nexus")]
    [InlineData("Demo · Manufacturing MX", "demo_manufacturing_mx")]
    [InlineData("Peña & Cía", "pena_cia")]
    [InlineData("2026 Training", "t_2026_training")]
    [InlineData("QA", "qa_co")]
    public void Slugs_come_from_the_name(string name, string slug)
    {
        Assert.Equal(slug, TenantAdminService.SlugFrom(name));
        Assert.True(TenantSlug.IsValid(slug));
    }

    private string? _lastEmail;

    /// <summary>A platform admin with no company, signed in through the API (a portal-only session).</summary>
    private async Task<(Guid UserId, string Token)> PlatformAdminAsync()
    {
        var email = $"padmin_{Guid.NewGuid():N}@example.test";
        var password = $"Pw-{Guid.NewGuid():N}1!";
        Guid userId;
        await using (var conn = await OpenAsync())
        {
            userId = await conn.ExecuteScalarAsync<Guid>(
                """
                insert into user_account (email, display_name, password_hash, password_changed_at, is_platform_admin)
                values (@email, 'Platform Admin', @hash, now(), true) returning id
                """,
                new { email, hash = fx.Services.GetRequiredService<IPasswordHasher>().Hash(password) });
        }

        var response = await fx.Client().PostAsJsonAsync("/api/auth/sign-in", new { email, password, remember = false });
        response.EnsureSuccessStatusCode();
        _lastEmail = email;
        return (userId, (await Json(response)).GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> TenantAsync(HttpClient client, string token, Guid id) =>
        await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, $"/api/admin/tenants/{id}", token)));

    private static string? Step(JsonElement t) => t.GetProperty("setup").GetProperty("step").GetString();

    private static string? Status(JsonElement t) => t.GetProperty("setup").GetProperty("status").GetString();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();
}
