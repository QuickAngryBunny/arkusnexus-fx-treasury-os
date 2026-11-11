using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Dashboard figures: dummy treasury data converted with the tenant's own FIX, tenant by tenant.</summary>
[Collection(PlatformCollection.Name)]
public sealed class DashboardTests(PlatformFixture fx)
{
    [Fact]
    public async Task Cash_exposure_and_flows_use_the_tenants_own_rates_and_accounts()
    {
        var a = await fx.ProvisionAsync("dash");
        var b = await fx.ProvisionAsync("dash");
        await using (var conn = await OpenAsync(a))
        {
            await ExecAsync(conn, """
                insert into fx_rate (source_code, rate_date, rate, raw_ref) values
                    ('banxico_fix', '2026-11-02', 18.00, 't'), ('banxico_fix', '2026-11-10', 20.00, 't');
                insert into legal_entity (id, name, country, functional_currency) values
                    ('00000000-0000-0000-0000-0000000000a1', 'MX SA', 'MX', 'MXN');
                insert into bank_account (id, legal_entity_id, bank_name, account_name, account_last4, currency, kind,
                                          opening_balance, opening_balance_date, feed_provider, feed_status) values
                    ('00000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000a1', 'B', 'USD', '1111', 'USD', 'operating', 1000, '2026-01-01', 'dummy', 'connected'),
                    ('00000000-0000-0000-0000-0000000000b2', '00000000-0000-0000-0000-0000000000a1', 'B', 'MXN', '2222', 'MXN', 'operating', 20000, '2026-01-01', 'dummy', 'connected');
                insert into financial_transaction (bank_account_id, booked_on, direction, amount, currency, category, counterparty, description, source) values
                    ('00000000-0000-0000-0000-0000000000b1', '2026-11-05', 'income', 500, 'USD', 'client_payment', 'Cliente X', 'x', 'manual'),
                    ('00000000-0000-0000-0000-0000000000b2', '2026-11-06', 'expense', 3800, 'MXN', 'payroll', 'Nómina', 'x', 'manual');
                """);
        }

        var dash = await GetAsync(a.Slug, "/api/tenant/dashboard");
        // Cash: USD 1,500 + MXN 16,200 / 20.00 (today's FIX) = 2,310 USD.
        Assert.Equal(2310m, dash.GetProperty("cash").GetProperty("totalUsd").GetDecimal());
        Assert.Equal(20m, dash.GetProperty("fix").GetProperty("rate").GetDecimal());
        // USD held by an MXN-functional entity is exposure.
        Assert.Equal(1500m, dash.GetProperty("exposure").GetProperty("usdLong").GetDecimal());
        // November flows at the month's average FIX (19.00): revenue 500 USD, expenses 3,800 / 19 = 200 USD.
        var flows = dash.GetProperty("flows");
        Assert.Equal(19m, flows.GetProperty("fix").GetDecimal());
        Assert.Equal(500m, flows.GetProperty("revenueUsd").GetDecimal());
        Assert.Equal(200m, flows.GetProperty("expensesUsd").GetDecimal());
        Assert.Equal(60m, flows.GetProperty("marginPct").GetDecimal());

        var customer = await GetAsync(a.Slug, "/api/tenant/dashboard?customer=Cliente%20X");
        Assert.Equal(100m, customer.GetProperty("flows").GetProperty("customerSharePct").GetDecimal());
        Assert.Equal(JsonValueKind.Null, customer.GetProperty("flows").GetProperty("expensesUsd").ValueKind);

        // Tenant B has no rates: its dashboard says so instead of reading A's.
        var empty = await fx.Client().SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/dashboard", b.Slug));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, empty.StatusCode);
    }

    private async Task<JsonElement> GetAsync(string tenant, string url)
    {
        var response = await fx.Client().SendAsync(PlatformFixture.Request(HttpMethod.Get, url, tenant));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<NpgsqlConnection> OpenAsync(TenantRecord tenant)
    {
        var password = await fx.Services.GetRequiredService<ISecretStore>().GetAsync(tenant.SecretRef!);
        var conn = new NpgsqlConnection(fx.Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(TenantServerRegistry.DefaultKey, tenant.DatabaseName!, tenant.LoginRole!, password!));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
