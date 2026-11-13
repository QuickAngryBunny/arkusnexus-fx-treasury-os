using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Api.Tenancy;
using FxOs.Platform.Data;
using FxOs.Platform.Migrations;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 0 exit criterion: a request for one tenant never reads the other.</summary>
[Collection(PlatformCollection.Name)]
public sealed class TenantIsolationTests(PlatformFixture fx)
{
    private const string SyncRequests = "/api/tenant/rates/banxico_fix/sync-requests";

    [Fact]
    public async Task Each_tenant_gets_its_own_database_and_login()
    {
        var a = await fx.ProvisionAsync("iso");
        var b = await fx.ProvisionAsync("iso");

        Assert.Equal(TenantStatus.Active, a.Status);
        Assert.Equal(TenantStatus.Active, b.Status);
        Assert.NotEqual(a.DatabaseName, b.DatabaseName);
        Assert.NotEqual(a.LoginRole, b.LoginRole);
        Assert.Equal<int?>(MigrationCatalog.LatestVersion(MigrationCatalog.Tenant), a.SchemaVersion);
    }

    [Fact]
    public async Task Requests_only_see_their_own_tenant_data()
    {
        var a = await fx.ProvisionAsync("iso");
        var b = await fx.ProvisionAsync("iso");
        var client = fx.Client();

        // A write in tenant A (audited in the same transaction) must not show up in tenant B.
        var created = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, SyncRequests, a.Slug, new { }));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        Assert.Contains("rates.sync_requested", await AuditActionsAsync(client, a.Slug));
        Assert.DoesNotContain("rates.sync_requested", await AuditActionsAsync(client, b.Slug));

        var profileA = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/profile", a.Slug));
        var profile = await profileA.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.Id, profile.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task A_tenant_login_cannot_open_another_tenant_database()
    {
        var a = await fx.ProvisionAsync("iso");
        var b = await fx.ProvisionAsync("iso");
        var password = await fx.Services.GetRequiredService<ISecretStore>().GetAsync(a.SecretRef!);
        var crossed = fx.Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(TenantServerRegistry.DefaultKey, b.DatabaseName!, a.LoginRole!, password!);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var conn = new NpgsqlConnection(crossed);
            await conn.OpenAsync();
        });

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Connection_factory_refuses_a_database_that_belongs_to_another_tenant()
    {
        var a = await fx.ProvisionAsync("iso");
        var b = await fx.ProvisionAsync("iso");

        // Simulate corrupted routing: tenant A pointed at tenant B's database and login.
        var forged = a.Clone();
        forged.DatabaseName = b.DatabaseName;
        forged.LoginRole = b.LoginRole;
        forged.SecretRef = b.SecretRef;

        var context = new TenantContext();
        context.Set(forged, "test");
        await using var sources = new TenantDataSources(
            fx.Services.GetRequiredService<TenantServerRegistry>(),
            fx.Services.GetRequiredService<ISecretStore>());
        var factory = new TenantConnectionFactory(context, sources);

        await Assert.ThrowsAsync<TenantIsolationException>(() => factory.OpenAsync());
    }

    [Fact]
    public async Task Every_tenant_route_requires_a_tenant()
    {
        var client = fx.Client();
        var routes = fx.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(TenantResolutionMiddleware.TenantPathPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" };
            foreach (var method in methods)
            {
                var response = await client.SendAsync(PlatformFixture.Request(new HttpMethod(method), route.RoutePattern.RawText!, tenant: null));
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                    $"{method} {route.RoutePattern.RawText} answered {(int)response.StatusCode} without a tenant.");
            }
        }
    }

    [Fact]
    public async Task Unknown_or_malformed_tenants_are_rejected()
    {
        var client = fx.Client();

        var unknown = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/profile", "nobody_here"));
        var malformed = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/profile", "Robert'); drop table--"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    private static async Task<string[]> AuditActionsAsync(HttpClient client, string tenant)
    {
        var response = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/audit", tenant));
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        return rows.EnumerateArray().Select(r => r.GetProperty("action").GetString()!).ToArray();
    }
}
