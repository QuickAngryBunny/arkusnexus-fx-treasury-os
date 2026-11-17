using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Sprint 2 step 4: forgot password and reset (T-07 to T-11, T-S1).</summary>
[Collection(PlatformCollection.Name)]
public sealed partial class PasswordResetTests(PlatformFixture fx)
{
    [Fact]
    public async Task Forgot_answers_the_same_and_emails_only_real_accounts()
    {
        var tenant = await fx.ProvisionAsync("forgot");
        var (_, email, _) = await MemberAsync(tenant);

        var known = await ForgotAsync(email.ToUpperInvariant(), "es-MX");
        var unknown = await ForgotAsync("nobody_" + email, "es-MX");
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        var emails = await OutboxAsync(email);
        var reset = Assert.Single(emails);
        Assert.Equal(("password_reset", "es-MX"), (reset.Template, reset.Locale));
        Assert.Empty(await OutboxAsync("nobody_" + email));
    }

    [Fact]
    public async Task Deactivated_members_get_no_reset_link()
    {
        var tenant = await fx.ProvisionAsync("fdeact");
        var (userId, email, _) = await MemberAsync(tenant);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync("update tenant_membership set status = 'deactivated', deactivated_at = now() where user_id = @userId", new { userId });
        }

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode);
        Assert.Empty(await OutboxAsync(email));
    }

    [Fact]
    public async Task A_second_request_within_a_minute_sends_nothing_more()
    {
        var tenant = await fx.ProvisionAsync("cool");
        var (_, email, _) = await MemberAsync(tenant);

        await ForgotAsync(email);
        await ForgotAsync(email);

        Assert.Single(await OutboxAsync(email));
    }

    [Fact]
    public async Task The_emailed_link_sets_a_new_password_once_and_signs_out_everywhere()
    {
        var tenant = await fx.ProvisionAsync("reset");
        var (_, email, oldPassword) = await MemberAsync(tenant);
        var client = fx.Client();
        var oldSession = await TokenAsync(email, oldPassword);

        await ForgotAsync(email);
        var link = ResetToken(await BodyAsync(Assert.Single(await OutboxAsync(email))));

        var described = await (await client.GetAsync($"/api/auth/password/reset?token={link}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("valid", described.GetProperty("state").GetString());
        Assert.Equal(email, described.GetProperty("email").GetString());

        // Rules are checked, including "not your email or name".
        var weak = await ResetAsync(link, email.Split('@')[0] + "-2026!");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var rules = (await weak.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rules").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(["personal_info"], rules);

        var newPassword = $"Nuevo-{Guid.NewGuid():N}-7?";
        var changed = await ResetAsync(link, newPassword);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The old session is gone, the new password works, the old one doesn't.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/auth/session", oldSession))).StatusCode);
        Assert.NotNull(await TokenAsync(email, newPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(email, oldPassword)).StatusCode);

        // "Password changed" email, and the link is spent.
        Assert.Contains(await OutboxAsync(email), e => e.Template == "password_changed");
        var again = await ResetAsync(link, $"Otra-{Guid.NewGuid():N}-8?");
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Equal("used", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Expired_and_unknown_links_show_the_expired_page()
    {
        var tenant = await fx.ProvisionAsync("rexp");
        var (userId, email, _) = await MemberAsync(tenant);
        await ForgotAsync(email);
        var link = ResetToken(await BodyAsync(Assert.Single(await OutboxAsync(email))));
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(
                "update account_token set created_at = now() - interval '2 hours', expires_at = now() - interval '1 hour' where user_id = @userId",
                new { userId });
        }

        var client = fx.Client();
        var expired = await (await client.GetAsync($"/api/auth/password/reset?token={link}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("expired", expired.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, expired.GetProperty("email").ValueKind);
        Assert.Equal(HttpStatusCode.Gone, (await ResetAsync(link, $"Late-{Guid.NewGuid():N}-9?")).StatusCode);

        var unknown = await (await client.GetAsync("/api/auth/password/reset?token=nope")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", unknown.GetProperty("state").GetString());
    }

    private Task<HttpResponseMessage> ForgotAsync(string email, string locale = "en-US") =>
        fx.Client().PostAsJsonAsync("/api/auth/password/forgot", new { email, locale });

    private Task<HttpResponseMessage> ResetAsync(string token, string password) =>
        fx.Client().PostAsJsonAsync("/api/auth/password/reset", new { token, password, locale = "en-US" });

    private Task<HttpResponseMessage> SignInAsync(string email, string password) =>
        fx.Client().PostAsJsonAsync("/api/auth/sign-in", new { email, password, remember = false });

    private async Task<string> TokenAsync(string email, string password)
    {
        var response = await SignInAsync(email, password);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<(Guid UserId, string Email, string Password)> MemberAsync(TenantRecord tenant)
    {
        var password = $"Pw-{Guid.NewGuid():N}1!";
        var (userId, email, _) = await fx.SignInAsync(tenant, "standard", fx.Services.GetRequiredService<IPasswordHasher>().Hash(password));
        return (userId, email, password);
    }

    private async Task<List<OutboxRow>> OutboxAsync(string email)
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<OutboxRow>(
            "select template, locale, body_protected from email_outbox where to_email = @email order by created_at",
            new { email = email.ToLowerInvariant() })).AsList();
    }

    /// <summary>Decrypts a queued body the way the dispatcher does, to follow the link a person would click.</summary>
    private Task<string> BodyAsync(OutboxRow row)
    {
        var protector = fx.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(EmailOutbox.ProtectorPurpose);
        return Task.FromResult(protector.Unprotect(row.BodyProtected!));
    }

    private static string ResetToken(string body) => ResetLink().Match(body).Groups[1].Value;

    [GeneratedRegex(@"reset-password\?token=([A-Za-z0-9_-]{43})")]
    private static partial Regex ResetLink();

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();

    private sealed class OutboxRow
    {
        public string Template { get; set; } = "";
        public string Locale { get; set; } = "";
        public string? BodyProtected { get; set; }
    }
}
