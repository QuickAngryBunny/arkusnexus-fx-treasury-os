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

/// <summary>Sprint 4: the first admin's setup link through the first-visit dashboard (J1, O-01 to O-05, O-S1, O-S2).</summary>
[Collection(PlatformCollection.Name)]
public sealed class OnboardingTests(PlatformFixture fx)
{
    [Fact]
    public async Task The_first_admin_sets_up_the_company_from_the_setup_email()
    {
        var (tenantId, email) = await NewTenantAsync();
        var token = PlatformFixture.LinkToken((await fx.EmailBodyAsync(email, "tenant_setup_invitation"))!, "setup");
        var client = fx.Client();

        // O-02: the link shows who it's for.
        var info = await Json(await client.GetAsync($"/api/auth/invitation?token={token}"));
        Assert.Equal(("valid", "tenant_setup", email, "admin"),
            (Str(info, "state"), Str(info, "kind"), Str(info, "email"), Str(info, "role")));

        var weak = await client.PostAsJsonAsync("/api/auth/invitation/accept", new { token, displayName = "Ana Ruiz", password = "short", locale = "en-US" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var accepted = await client.PostAsJsonAsync("/api/auth/invitation/accept",
            new { token, displayName = "Ana Ruiz", password = "Tipo-de-cambio-26!", locale = "en-US" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var session = Str(await Json(accepted), "token")!;

        // The link works once.
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/api/auth/invitation/accept",
            new { token, displayName = "Ana Ruiz", password = "Tipo-de-cambio-26!" })).StatusCode);

        var profile = await Get(client, session, "/api/tenant/profile");
        Assert.Equal("admin", Str(profile, "role"));
        Assert.False(profile.GetProperty("onboarding").GetProperty("companyConfirmed").GetBoolean());
        Assert.Equal("en-US", profile.GetProperty("me").GetProperty("language").GetString());

        // O-03: company. Finishing before it is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, session, "/api/tenant/onboarding/complete", new { })).StatusCode);
        var company = await Put(client, session, "/api/tenant/company", new
        {
            displayName = "Arkus Test", legalName = "Arkus Test S.A. de C.V.", country = "MX", timeZone = "America/Tijuana",
            conversionDirection = "usd_to_mxn", locale = "es-MX", baseCurrency = "USD", reportingCurrency = "MXN", confirmOnboardingStep = true,
        });
        Assert.Equal(HttpStatusCode.NoContent, company.StatusCode);

        // O-04 / O-S2: a bank of each currency is required.
        await Put(client, session, "/api/tenant/banks", new { banks = new[] { new { name = "BBVA Operativa", currency = "MXN" } } });
        var missing = await Post(client, session, "/api/tenant/onboarding/complete", new { locale = "es-MX" });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var missingBody = await missing.Content.ReadAsStringAsync();
        Assert.True(missingBody.Contains("banks_missing", StringComparison.Ordinal), missingBody);

        var banks = await Json(await Put(client, session, "/api/tenant/banks", new
        {
            banks = new object[]
            {
                new { name = "BBVA Operativa", currency = "MXN" }, new { name = "bbva operativa", currency = "MXN" },
                new { name = "Chase Business", currency = "USD" },
            },
        }));
        Assert.Equal(2, banks.GetProperty("banks").GetArrayLength()); // the duplicate name is ignored

        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, session, "/api/tenant/onboarding/complete", new { locale = "es-MX" })).StatusCode);
        var done = await Get(client, session, "/api/tenant/profile");
        Assert.True(done.GetProperty("onboarding").GetProperty("completed").GetBoolean());
        Assert.Equal("Arkus Test", Str(done, "displayName"));
        Assert.NotNull(await fx.EmailBodyAsync(email, "welcome"));

        // The company name is also the tenant's name in the control plane.
        await using var conn = await OpenAsync();
        Assert.Equal("Arkus Test", await conn.ExecuteScalarAsync<string>("select display_name from tenant where id = @tenantId", new { tenantId }));
    }

    [Fact]
    public async Task An_expired_setup_link_sends_a_fresh_one_once()
    {
        var (tenantId, email) = await NewTenantAsync();
        var token = PlatformFixture.LinkToken((await fx.EmailBodyAsync(email, "tenant_setup_invitation"))!, "setup");
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update invitation set created_at = now() - interval '8 days', expires_at = now() - interval '1 day' where tenant_id = @tenantId",
                new { tenantId });
        }

        var client = fx.Client();
        var info = await Json(await client.GetAsync($"/api/auth/invitation?token={token}"));
        Assert.Equal("expired", Str(info, "state"));
        Assert.Equal(JsonValueKind.Null, info.GetProperty("email").ValueKind);

        var renewed = await Json(await client.PostAsJsonAsync("/api/auth/invitation/renew", new { token }));
        Assert.True(renewed.GetProperty("sent").GetBoolean());
        var again = await Json(await client.PostAsJsonAsync("/api/auth/invitation/renew", new { token }));
        Assert.False(again.GetProperty("sent").GetBoolean());

        var fresh = PlatformFixture.LinkToken((await fx.EmailBodyAsync(email, "tenant_setup_invitation"))!, "setup");
        Assert.NotEqual(token, fresh);
        Assert.Equal("valid", Str(await Json(await client.GetAsync($"/api/auth/invitation?token={fresh}")), "state"));

        // A renewed link that expires too can't be renewed again: someone has to invite again.
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update invitation set created_at = now() - interval '8 days', expires_at = now() - interval '1 day' where tenant_id = @tenantId and revoked_at is null",
                new { tenantId });
        }

        Assert.False((await Json(await client.PostAsJsonAsync("/api/auth/invitation/renew", new { token = fresh }))).GetProperty("sent").GetBoolean());
    }

    [Fact]
    public async Task An_invitation_that_expired_long_ago_is_not_renewed()
    {
        var (tenantId, email) = await NewTenantAsync();
        var token = PlatformFixture.LinkToken((await fx.EmailBodyAsync(email, "tenant_setup_invitation"))!, "setup");
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update invitation set created_at = now() - interval '45 days', expires_at = now() - interval '38 days' where tenant_id = @tenantId",
                new { tenantId });
        }

        var client = fx.Client();
        Assert.Equal("expired", Str(await Json(await client.GetAsync($"/api/auth/invitation?token={token}")), "state"));
        Assert.False((await Json(await client.PostAsJsonAsync("/api/auth/invitation/renew", new { token }))).GetProperty("sent").GetBoolean());
    }

    [Fact]
    public async Task Company_and_banks_are_admins_only()
    {
        var tenant = await fx.ProvisionAsync("cadm");
        var (_, _, standard) = await fx.SignInAsync(tenant, "standard");
        var client = fx.Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(client, standard, "/api/tenant/banks",
            new { banks = new[] { new { name = "X", currency = "MXN" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/company", standard))).StatusCode);
    }

    private async Task<(Guid TenantId, string Email)> NewTenantAsync()
    {
        var email = $"first_{Guid.NewGuid():N}@example.test";
        var slug = $"onb_{Guid.NewGuid():N}"[..12];
        var tenant = await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Onboarding Co", isDummy: true);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "insert into tenant_job (tenant_id, kind, step, first_admin_email) values (@id, 'setup', 'invitation', @email)",
                new { id = tenant.Id, email });
        }

        await fx.Services.GetRequiredService<TenantSetupRunner>().RunOnceAsync();
        return (tenant.Id, email);
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, string token, string url, object body) =>
        client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, url, token, body));

    private static Task<HttpResponseMessage> Post(HttpClient client, string token, string url, object body) =>
        client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, url, token, body));

    private static async Task<JsonElement> Get(HttpClient client, string token, string url) =>
        await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, url, token)));

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    private async Task<NpgsqlConnection> OpenAsync() => await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();
}
