using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FxOs.Api.Tenancy;
using FxOs.Platform.Data;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Sprints 5–7: team (J4/J5), settings (U-01 to U-03, C-01) and role checks on every admin-only route.</summary>
[Collection(PlatformCollection.Name)]
public sealed class TeamAndSettingsTests(PlatformFixture fx)
{
    [Fact]
    public async Task An_admin_invites_a_teammate_who_joins_and_is_signed_in()
    {
        var tenant = await fx.ProvisionAsync("team");
        var (adminId, adminEmail, admin) = await fx.SignInAsync(tenant, "admin");
        var client = fx.Client();
        var email = $"mariana_{Guid.NewGuid():N}@example.test";

        var invite = await Post(client, admin, "/api/tenant/team/invitations",
            new { displayName = "Mariana López", email, role = "approver", note = "You'll approve our plans.", locale = "en-US" });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);

        var team = await Get(client, admin, "/api/tenant/team");
        var pending = Assert.Single(team.GetProperty("invitations").EnumerateArray());
        Assert.Equal(("pending", "approver"), (Str(pending, "state"), Str(pending, "role")));

        var body = (await fx.EmailBodyAsync(email, "teammate_invitation"))!;
        var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("“You'll approve our plans.”", text, StringComparison.Ordinal);
        var token = PlatformFixture.LinkToken(body, "join");

        var info = await Json(await client.GetAsync($"/api/auth/invitation?token={token}"));
        Assert.Equal(("teammate", tenant.DisplayName), (Str(info, "kind"), Str(info, "company")));

        var joined = await client.PostAsJsonAsync("/api/auth/invitation/accept",
            new { token, displayName = "Mariana López", password = "Aprobar-planes-26!", locale = "en-US" });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var session = await Get(client, Str(await Json(joined), "token")!, "/api/auth/session");
        Assert.Equal(("approver", tenant.Slug), (Str(session, "role"), session.GetProperty("tenant").GetProperty("slug").GetString()));

        Assert.NotNull(await fx.EmailBodyAsync(adminEmail, "member_joined"));
        Assert.NotNull(await fx.EmailBodyAsync(email, "welcome"));

        var after = await Get(client, admin, "/api/tenant/team");
        Assert.Empty(after.GetProperty("invitations").EnumerateArray());
        Assert.Contains(after.GetProperty("members").EnumerateArray(), m => Str(m, "email") == email && Str(m, "role") == "approver");

        // T-S3: already in the company.
        var again = await Post(client, admin, "/api/tenant/team/invitations", new { displayName = "Mariana", email, role = "standard" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_member", Str(await Json(again), "code"));
        _ = adminId;
    }

    [Fact]
    public async Task Someone_with_an_account_joins_a_second_company_but_signs_in_with_their_own_password()
    {
        var first = await fx.ProvisionAsync("acc1");
        var second = await fx.ProvisionAsync("acc2");
        var (_, existingEmail, _) = await fx.SignInAsync(first, "standard", fx.Services.GetRequiredService<IPasswordHasher>().Hash("Original-pass-26!"));
        var (_, _, admin) = await fx.SignInAsync(second, "admin");
        var client = fx.Client();

        await Post(client, admin, "/api/tenant/team/invitations", new { displayName = "Existing", email = existingEmail, role = "standard" });
        var token = PlatformFixture.LinkToken((await fx.EmailBodyAsync(existingEmail, "teammate_invitation"))!, "join");
        var joined = await client.PostAsJsonAsync("/api/auth/invitation/accept", new { token, password = "Ignored-pass-26!" });
        Assert.Equal(HttpStatusCode.Accepted, joined.StatusCode);
        Assert.Equal("sign_in_required", Str(await Json(joined), "code"));

        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { email = existingEmail, password = "Original-pass-26!" });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = existingEmail, password = "Ignored-pass-26!" })).StatusCode);
    }

    [Fact]
    public async Task Roles_deactivation_and_the_last_admin()
    {
        var tenant = await fx.ProvisionAsync("roles");
        var (adminId, _, admin) = await fx.SignInAsync(tenant, "admin");
        var (memberId, memberEmail, member) = await fx.SignInAsync(tenant, "standard");
        var client = fx.Client();

        Assert.Equal(HttpStatusCode.OK, (await Post(client, admin, $"/api/tenant/team/members/{memberId}/role", new { role = "approver" })).StatusCode);
        Assert.NotNull(await fx.EmailBodyAsync(memberEmail, "role_changed"));
        Assert.Equal("approver", Str(await Get(client, member, "/api/auth/session"), "role"));

        // The only admin can't step down or be deactivated, and can't deactivate themselves.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, admin, $"/api/tenant/team/members/{adminId}/role", new { role = "standard" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, admin, $"/api/tenant/team/members/{adminId}/deactivate", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Post(client, admin, $"/api/tenant/team/members/{memberId}/deactivate", new { })).StatusCode);
        Assert.NotNull(await fx.EmailBodyAsync(memberEmail, "account_deactivated"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", member))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Post(client, admin, $"/api/tenant/team/members/{memberId}/reactivate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, admin, $"/api/tenant/team/members/{memberId}/password-reset", new { })).StatusCode);
    }

    [Fact]
    public async Task Admins_only_reach_their_own_company_members_and_invitations()
    {
        var a = await fx.ProvisionAsync("xteama");
        var b = await fx.ProvisionAsync("xteamb");
        var (_, _, adminA) = await fx.SignInAsync(a, "admin");
        var (memberB, _, _) = await fx.SignInAsync(b, "standard");
        var (_, _, adminB) = await fx.SignInAsync(b, "admin");
        var client = fx.Client();
        await Post(client, adminB, "/api/tenant/team/invitations", new { displayName = "Invitee", email = $"i_{Guid.NewGuid():N}@example.test", role = "standard" });
        var invitationB = (await Get(client, adminB, "/api/tenant/team")).GetProperty("invitations")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, adminA, $"/api/tenant/team/members/{memberB}/role", new { role = "approver" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, adminA, $"/api/tenant/team/members/{memberB}/deactivate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, adminA, $"/api/tenant/team/members/{memberB}/password-reset", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, adminA, $"/api/tenant/team/invitations/{invitationB}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, adminA, $"/api/tenant/team/invitations/{invitationB}/resend", new { })).StatusCode);
        Assert.DoesNotContain((await Get(client, adminA, "/api/tenant/team")).GetProperty("members").EnumerateArray(),
            m => m.GetProperty("userId").GetGuid() == memberB);
    }

    [Fact]
    public async Task Profile_notifications_and_password_change()
    {
        var tenant = await fx.ProvisionAsync("prefs");
        var hasher = fx.Services.GetRequiredService<IPasswordHasher>();
        var (userId, email, token) = await fx.SignInAsync(tenant, "standard", hasher.Hash("Current-pass-26!"));
        var other = (await fx.Client().PostAsJsonAsync("/api/auth/sign-in", new { email, password = "Current-pass-26!" }));
        var otherToken = Str(await Json(other), "token")!;
        var client = fx.Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, token, "/api/tenant/me",
            new { displayName = "Leo", language = "en-US", numberFormat = "period_decimal", timeZone = "Mars/Olympus" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(client, token, "/api/tenant/me",
            new { displayName = "Leo Díaz", language = "en-US", numberFormat = "comma_decimal", timeZone = "America/Tijuana" })).StatusCode);
        var me = await Get(client, token, "/api/tenant/me");
        Assert.Equal(("Leo Díaz", "comma_decimal", "America/Tijuana"), (Str(me, "displayName"), Str(me, "numberFormat"), Str(me, "timeZone")));
        Assert.Equal("Leo Díaz", Str((await Get(client, token, "/api/auth/session")).GetProperty("user"), "displayName"));

        // Notifications: only changes are stored.
        await Put(client, token, "/api/tenant/me/notifications", new { items = new[] { new { code = "weekly_forecast_summary", enabled = true }, new { code = "forecast_move", enabled = true } } });
        var list = (await Get(client, token, "/api/tenant/me/notifications")).EnumerateArray().ToList();
        Assert.True(list.Single(n => Str(n, "code") == "weekly_forecast_summary").GetProperty("enabled").GetBoolean());
        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("select count(*) from notification_preference where user_id = @userId", new { userId }));
        }

        // Password: the current one is required; other sessions end, this one stays.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, token, "/api/auth/password/change", new { current = "Wrong-pass-26!", password = "Brand-new-pass-26!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, token, "/api/auth/password/change", new { current = "Current-pass-26!", password = "Brand-new-pass-26!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", otherToken))).StatusCode);
        Assert.NotNull(await fx.EmailBodyAsync(email, "password_changed"));
    }

    [Fact]
    public async Task Email_change_takes_effect_only_after_the_confirmation_link()
    {
        var tenant = await fx.ProvisionAsync("emch");
        var hasher = fx.Services.GetRequiredService<IPasswordHasher>();
        var (userId, oldEmail, token) = await fx.SignInAsync(tenant, "standard", hasher.Hash("Current-pass-26!"));
        var (_, takenEmail, _) = await fx.SignInAsync(tenant, "standard");
        var newEmail = $"new_{Guid.NewGuid():N}@example.test";
        var client = fx.Client();
        await Put(client, token, "/api/tenant/me", new { displayName = "Test member", language = "es-MX", numberFormat = "period_decimal", timeZone = "America/Mexico_City" });

        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, token, "/api/auth/email/change", new { email = takenEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, token, "/api/auth/email/change", new { email = newEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = oldEmail, password = "Current-pass-26!" })).StatusCode);

        var link = PlatformFixture.LinkToken((await fx.EmailBodyAsync(newEmail, "email_change_confirmation"))!, "confirm-email");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/email/confirm", new { token = link })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = newEmail, password = "Current-pass-26!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/api/auth/email/confirm", new { token = link })).StatusCode);

        await using var conn = await fx.OpenTenantAsync(tenant);
        Assert.Equal(newEmail, await conn.ExecuteScalarAsync<string>("select email from user_profile where user_id = @userId", new { userId }));
    }

    [Fact]
    public async Task After_onboarding_the_last_bank_of_a_currency_stays_and_renames_keep_the_bank()
    {
        var tenant = await fx.ProvisionAsync("bnk");
        var (_, _, admin) = await fx.SignInAsync(tenant, "admin");
        var client = fx.Client();
        var company = await Json(await Put(client, admin, "/api/tenant/banks", new { banks = new[] { new { name = "Peso Uno", currency = "MXN" }, new { name = "Dólar Uno", currency = "USD" } } }));
        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            await conn.ExecuteAsync("update tenant_settings set company_confirmed_at = now(), onboarding_completed_at = now() where id = 1");
        }

        var mxn = company.GetProperty("banks").EnumerateArray().First(b => Str(b, "currency") == "MXN");
        var renamed = await Json(await Put(client, admin, "/api/tenant/banks", new
        {
            banks = new object[] { new { id = mxn.GetProperty("id").GetGuid(), name = "Peso Renombrado", currency = "MXN" }, new { name = "Dólar Uno", currency = "USD" } },
        }));
        var after = renamed.GetProperty("banks").EnumerateArray().First(b => Str(b, "currency") == "MXN");
        Assert.Equal((mxn.GetProperty("id").GetGuid(), "Peso Renombrado"), (after.GetProperty("id").GetGuid(), Str(after, "name")));

        var removeLast = await Put(client, admin, "/api/tenant/banks", new { banks = new[] { new { name = "Dólar Uno", currency = "USD" } } });
        Assert.Equal(HttpStatusCode.Conflict, removeLast.StatusCode);
        Assert.Equal("last_bank", Str(await Json(removeLast), "code"));
    }

    [Fact]
    public async Task Every_admin_only_route_refuses_approvers_and_standard_members()
    {
        var tenant = await fx.ProvisionAsync("rbac");
        var (_, _, approver) = await fx.SignInAsync(tenant, "approver");
        var (_, _, standard) = await fx.SignInAsync(tenant, "standard");
        var client = fx.Client();
        var routes = fx.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<CompanyAdminOnlyAttribute>() is not null)
            .ToList();

        Assert.True(routes.Count >= 10, $"Only {routes.Count} admin-only routes found.");
        foreach (var route in routes)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(route.RoutePattern.RawText!, @"\{[^}]+\}", Guid.NewGuid().ToString());
            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                foreach (var token in new[] { approver, standard })
                {
                    var response = await client.SendAsync(PlatformFixture.Authed(new HttpMethod(method), url, token, new { }));
                    Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} answered {(int)response.StatusCode} to a non-admin.");
                }
            }
        }
    }

    [Fact]
    public async Task The_portal_api_refuses_company_admins_who_are_not_platform_admins()
    {
        var tenant = await fx.ProvisionAsync("noportal");
        var (_, _, admin) = await fx.SignInAsync(tenant, "admin");
        var client = fx.Client();
        var routes = fx.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/admin", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(route.RoutePattern.RawText!, @"\{[^}]+\}", Guid.NewGuid().ToString());
            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var response = await client.SendAsync(PlatformFixture.Authed(new HttpMethod(method), url, admin, new { }));
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} answered {(int)response.StatusCode} to a company admin.");
            }
        }
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, string token, string url, object body) =>
        client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, url, token, body));

    private static Task<HttpResponseMessage> Post(HttpClient client, string token, string url, object body) =>
        client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, url, token, body));

    private static async Task<JsonElement> Get(HttpClient client, string token, string url) =>
        await Json(await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, url, token)));

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();
}
