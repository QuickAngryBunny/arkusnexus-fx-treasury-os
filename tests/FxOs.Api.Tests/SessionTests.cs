using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Sprint 2 step 1: tenant routes take the company from the signed-in session.</summary>
[Collection(PlatformCollection.Name)]
public sealed class SessionTests(PlatformFixture fx)
{
    [Fact]
    public async Task A_session_opens_its_own_company_and_ignores_a_tenant_header()
    {
        var mine = await fx.ProvisionAsync("sess");
        var other = await fx.ProvisionAsync("sess");
        var (_, _, token) = await fx.SignInAsync(mine);

        var request = PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token);
        request.Headers.Add("X-Tenant", other.Slug);
        var response = await fx.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mine.Slug, profile.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Writes_are_audited_as_the_signed_in_user()
    {
        var tenant = await fx.ProvisionAsync("actor");
        var (userId, _, token) = await fx.SignInAsync(tenant);
        var client = fx.Client();

        var created = await client.SendAsync(
            PlatformFixture.Authed(HttpMethod.Post, "/api/tenant/rates/banxico_fix/sync-requests", token, new { }));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        var audit = await (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/audit", token)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var entry = audit.EnumerateArray().First(e => e.GetProperty("action").GetString() == "rates.sync_requested");
        Assert.Equal($"user:{userId:D}", entry.GetProperty("actor").GetString());
    }

    [Theory]
    [InlineData("update session set expires_at = now() - interval '1 second', created_at = now() - interval '1 day' where user_id = @u")]
    [InlineData("update session set revoked_at = now(), revoked_reason = 'sign_out' where user_id = @u")]
    [InlineData("update tenant_membership set status = 'deactivated', deactivated_at = now() where user_id = @u")]
    [InlineData("update tenant set status = 'archived', archived_at = now() where id = @t")]
    public async Task Ended_sessions_and_removed_access_are_refused(string change)
    {
        var tenant = await fx.ProvisionAsync("ended");
        var (userId, _, token) = await fx.SignInAsync(tenant);
        var client = fx.Client();
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token))).StatusCode);

        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(change, new { u = userId, t = tenant.Id });
        }

        var response = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_developer_switcher_never_opens_a_real_data_tenant()
    {
        var slug = $"real_{Guid.NewGuid():N}"[..14];
        var real = await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Real company", isDummy: false);

        var response = await fx.Client().SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/profile", real.Slug));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // A member's session still opens it.
        var (_, _, token) = await fx.SignInAsync(real);
        Assert.Equal(HttpStatusCode.OK,
            (await fx.Client().SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/profile", token))).StatusCode);
    }

    [Fact]
    public async Task Session_tokens_are_stored_only_as_hashes()
    {
        var tenant = await fx.ProvisionAsync("hashes");
        var (userId, _, token) = await fx.SignInAsync(tenant);

        await using var conn = await OpenAsync();
        var stored = await conn.QuerySingleAsync<byte[]>("select token_hash from session where user_id = @userId", new { userId });
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)), stored);
    }

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();
}
