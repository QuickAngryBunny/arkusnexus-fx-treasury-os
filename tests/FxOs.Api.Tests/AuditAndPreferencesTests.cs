using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FxOs.Platform.Audit;
using FxOs.Platform.Email;
using Microsoft.Extensions.DependencyInjection;

namespace FxOs.Api.Tests;

/// <summary>
/// Phase 3 gaps: the company's own audit log gets its sign-ins, invitations and role changes; emails use the person's
/// time zone; approval-request emails are offered to approvers and admins only.
/// </summary>
[Collection(PlatformCollection.Name)]
public sealed class AuditAndPreferencesTests(PlatformFixture fx)
{
    [Fact]
    public async Task Sign_ins_invitations_and_role_changes_reach_the_company_audit_log_once()
    {
        var tenant = await fx.ProvisionAsync("audm");
        var other = await fx.ProvisionAsync("audo");
        var (_, _, admin) = await fx.SignInAsync(tenant, "admin");
        var (memberId, _, _) = await fx.SignInAsync(tenant, "standard");
        var client = fx.Client();

        Assert.Equal(HttpStatusCode.OK, (await Post(client, admin, "/api/tenant/team/invitations",
            new { displayName = "Nuevo", email = $"nuevo_{Guid.NewGuid():N}@example.test", role = "standard", locale = "en-US" })).StatusCode);
        Assert.True((await Post(client, admin, $"/api/tenant/team/members/{memberId}/role", new { role = "approver", locale = "en-US" })).IsSuccessStatusCode);

        // A sign-in through the API (the fixture's sessions are made directly).
        var password = $"Pw-{Guid.NewGuid():N}1!";
        var (_, email, _) = await fx.SignInAsync(tenant, "standard",
            fx.Services.GetRequiredService<FxOs.Platform.Identity.IPasswordHasher>().Hash(password));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/sign-in", new { email, password })).StatusCode);

        var mirror = fx.Services.GetRequiredService<ControlAuditMirror>();
        await mirror.RunOnceAsync();
        await mirror.RunOnceAsync();

        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            var actions = (await conn.QueryAsync<string>(
                "select action from audit_log where control_audit_id is not null")).ToList();
            Assert.Contains("invitation.sent", actions);
            Assert.Contains("membership.role_changed", actions);
            Assert.Contains("account.signed_in", actions);
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>(
                "select count(*) - count(distinct control_audit_id) from audit_log where control_audit_id is not null"));
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>(
                "select count(*) from audit_log where control_audit_id is not null and action like 'tenant.%'"));
        }

        await using (var conn = await fx.OpenTenantAsync(other))
        {
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>(
                "select count(*) from audit_log where control_audit_id is not null and data->>'email' like 'nuevo_%'"));
        }
    }

    [Fact]
    public async Task Email_times_use_the_profile_then_the_confirmed_company_zone()
    {
        var tenant = await fx.ProvisionAsync("eclk");
        var (userId, _, token) = await fx.SignInAsync(tenant, "standard");
        var clocks = fx.Services.GetRequiredService<EmailClocks>();

        // Nothing chosen yet: the company's zone is only a default before onboarding step 2.
        Assert.Equal(EmailClock.MexicoCity, await clocks.ForUserAsync(userId, tenant.Id));
        Assert.Equal(EmailClock.MexicoCity, await clocks.ForCompanyAsync(tenant.Id));

        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            await conn.ExecuteAsync("update tenant_settings set time_zone = 'America/Cancun', company_confirmed_at = now() where id = 1");
        }

        Assert.Equal("America/Cancun", (await clocks.ForCompanyAsync(tenant.Id)).TimeZone);

        var client = fx.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, "/api/tenant/me", token,
            new { displayName = "Zona", language = "es-MX", numberFormat = "comma_decimal", timeZone = "America/Tijuana" }))).StatusCode);
        Assert.Equal("America/Tijuana", (await clocks.ForUserAsync(userId, tenant.Id)).TimeZone);
        // No company given (forgot password, reset): the one used last.
        Assert.Equal("America/Tijuana", (await clocks.ForUserAsync(userId, null)).TimeZone);
    }

    [Fact]
    public async Task Approval_request_emails_are_offered_to_approvers_and_admins_only()
    {
        var tenant = await fx.ProvisionAsync("ntfr");
        var (_, _, standard) = await fx.SignInAsync(tenant, "standard");
        var (_, _, approver) = await fx.SignInAsync(tenant, "approver");
        var client = fx.Client();

        var codes = async (string token) => (await Json(await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/me/notifications", token))))
            .EnumerateArray().Select(n => n.GetProperty("code").GetString()).ToList();

        Assert.DoesNotContain("approval_request", await codes(standard));
        Assert.Contains("approval_request", await codes(approver));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, "/api/tenant/me/notifications", standard,
            new { items = new[] { new { code = "approval_request", enabled = false } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, "/api/tenant/me/notifications", approver,
            new { items = new[] { new { code = "approval_request", enabled = false } } }))).StatusCode);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string token, string url, object body) =>
        client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, url, token, body));

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
}
