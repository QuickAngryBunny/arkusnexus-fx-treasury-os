using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Migrations;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ProvisioningAndAuditTests(PlatformFixture fx)
{
    [Fact]
    public async Task Provisioning_twice_is_a_no_op()
    {
        var first = await fx.ProvisionAsync("prov");
        var again = await fx.Services.GetRequiredService<TenantProvisioner>()
            .ProvisionAsync(first.Slug, first.DisplayName, isDummy: true);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.DatabaseName, again.DatabaseName);
        Assert.Equal(TenantStatus.Active, again.Status);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("1starts_with_digit")]
    [InlineData("UPPER")]
    [InlineData("has-dash")]
    [InlineData("x23456789012345678901234567890123")]
    public async Task Invalid_slugs_never_reach_the_database(string slug)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Bad"));
    }

    [Fact]
    public void Migration_sets_are_numbered_without_gaps()
    {
        foreach (var set in new[] { MigrationCatalog.ControlPlane, MigrationCatalog.Tenant })
        {
            var migrations = MigrationCatalog.Load(set);
            Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        }
    }

    [Fact]
    public async Task Writes_are_audited_and_the_audit_log_is_append_only()
    {
        var tenant = await fx.ProvisionAsync("audit");
        var client = fx.Client();

        var created = await client.SendAsync(
            PlatformFixture.Request(HttpMethod.Post, "/api/tenant/departments", tenant.Slug, new { name = "Finanzas" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var auditResponse = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/audit", tenant.Slug));
        var audit = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("department.created", actions);
        Assert.Contains("tenant.provisioned", actions);

        await using var conn = await OpenAsTenantAsync(tenant);
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("update audit_log set actor = 'someone else'", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    private async Task<NpgsqlConnection> OpenAsTenantAsync(TenantRecord tenant)
    {
        var password = await fx.Services.GetRequiredService<ISecretStore>().GetAsync(tenant.SecretRef!);
        var cs = fx.Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(tenant.ServerKey!, tenant.DatabaseName!, tenant.LoginRole!, password!);
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }
}
