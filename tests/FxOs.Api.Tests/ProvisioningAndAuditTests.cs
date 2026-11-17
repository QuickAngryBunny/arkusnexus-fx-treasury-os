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
            PlatformFixture.Request(HttpMethod.Post, "/api/tenant/rates/banxico_fix/sync-requests", tenant.Slug, new { }));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        var auditResponse = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/audit", tenant.Slug));
        var audit = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("rates.sync_requested", actions);
        Assert.Contains("tenant.provisioned", actions);

        await using var conn = await OpenAsTenantAsync(tenant);
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("update audit_log set actor = 'someone else'", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Treasury_tables_are_gone_from_tenant_databases()
    {
        // Phase 3 scope: no treasury, legal entities or departments (V0008). Rates, forecasts and news stay.
        var tenant = await fx.ProvisionAsync("scope");
        await using var conn = await OpenAsTenantAsync(tenant);
        await using var cmd = new NpgsqlCommand(
            "select table_name from information_schema.tables where table_schema = 'public'", conn);
        var tables = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var removed in new[] { "financial_transaction", "bank_account", "legal_entity", "department" })
        {
            Assert.DoesNotContain(removed, tables);
        }

        foreach (var kept in new[] { "tenant_settings", "user_profile", "bank", "notification_preference", "audit_log", "fx_rate", "forecast_run", "news_article" })
        {
            Assert.Contains(kept, tables);
        }
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
