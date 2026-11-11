using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 2 dummy treasury data: accounts, balances and movements stay inside their tenant.</summary>
[Collection(PlatformCollection.Name)]
public sealed class TreasuryEndpointsTests(PlatformFixture fx)
{
    [Fact]
    public async Task Each_tenant_sees_only_its_own_accounts_and_movements()
    {
        var a = await fx.ProvisionAsync("trs");
        var b = await fx.ProvisionAsync("trs");
        await using (var conn = await OpenAsync(a))
        {
            var account = await AddAccountAsync(conn, "Cuenta A", "USD", 1000m);
            await AddTxnAsync(conn, account, "2026-10-05", "income", 500m, "USD", "client_payment");
            await AddTxnAsync(conn, account, "2026-10-20", "expense", 200m, "USD", "cloud_software");
        }

        await using (var conn = await OpenAsync(b))
        {
            await AddAccountAsync(conn, "Cuenta B", "MXN", 7m);
        }

        var client = fx.Client();
        var accountsA = await GetAsync(client, "/api/tenant/bank-accounts", a.Slug);
        var accountsB = await GetAsync(client, "/api/tenant/bank-accounts", b.Slug);
        var txnsB = await GetAsync(client, "/api/tenant/transactions", b.Slug);

        var only = Assert.Single(accountsA.EnumerateArray());
        Assert.Equal("Cuenta A", only.GetProperty("accountName").GetString());
        Assert.Equal(1300m, only.GetProperty("balance").GetDecimal());
        Assert.Equal("USD", only.GetProperty("currency").GetString());
        Assert.Equal("connected", only.GetProperty("feed").GetProperty("status").GetString());
        Assert.Equal("Cuenta B", Assert.Single(accountsB.EnumerateArray()).GetProperty("accountName").GetString());
        Assert.Equal(0, txnsB.GetArrayLength());
    }

    [Fact]
    public async Task Monthly_summary_keeps_currencies_apart_and_transfers_out_of_income_and_expense()
    {
        var a = await fx.ProvisionAsync("trs");
        await using (var conn = await OpenAsync(a))
        {
            var usd = await AddAccountAsync(conn, "USD", "USD", 0m);
            var mxn = await AddAccountAsync(conn, "MXN", "MXN", 0m);
            await AddTxnAsync(conn, usd, "2026-10-05", "income", 1000m, "USD", "client_payment");
            await AddTxnAsync(conn, usd, "2026-10-25", "income", 400m, "USD", "intercompany");
            await AddTxnAsync(conn, mxn, "2026-10-15", "expense", 9000m, "MXN", "payroll");
            await AddConversionAsync(conn, usd, mxn, "2026-10-26", 100m, 1850m, 18.5m);
        }

        var client = fx.Client();
        var months = await GetAsync(client, "/api/tenant/cash-flow/monthly?months=3", a.Slug);
        var oct = months.EnumerateArray().Where(m => m.GetProperty("month").GetString() == "2026-10")
            .ToDictionary(m => m.GetProperty("currency").GetString()!);

        Assert.Equal(1000m, oct["USD"].GetProperty("income").GetDecimal());
        Assert.Equal(0m, oct["USD"].GetProperty("expense").GetDecimal());
        Assert.Equal(400m, oct["USD"].GetProperty("transfersIn").GetDecimal());
        Assert.Equal(100m, oct["USD"].GetProperty("transfersOut").GetDecimal());
        Assert.Equal(9000m, oct["MXN"].GetProperty("expense").GetDecimal());
        Assert.Equal(1850m, oct["MXN"].GetProperty("transfersIn").GetDecimal());

        var conversions = await GetAsync(client, "/api/tenant/transactions?direction=income&limit=10", a.Slug);
        var leg = conversions.EnumerateArray().Single(t => t.GetProperty("category").GetString() == "fx_conversion");
        Assert.Equal(18.5m, leg.GetProperty("conversion").GetProperty("appliedRate").GetDecimal());
        Assert.Equal("USD", leg.GetProperty("conversion").GetProperty("counterCurrency").GetString());

        var bad = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/transactions?direction=sideways", a.Slug));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_movement_in_another_currency_than_its_account_is_refused()
    {
        var a = await fx.ProvisionAsync("trs");
        await using var conn = await OpenAsync(a);
        var usd = await AddAccountAsync(conn, "USD", "USD", 0m);
        await Assert.ThrowsAsync<PostgresException>(() => AddTxnAsync(conn, usd, "2026-10-05", "income", 1m, "MXN", "client_payment"));
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url, string tenant)
    {
        var response = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, url, tenant));
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

    private static async Task<Guid> AddAccountAsync(NpgsqlConnection conn, string name, string currency, decimal opening)
    {
        await using var le = new NpgsqlCommand(
            "insert into legal_entity (name, country, functional_currency) values ('Entidad ' || @n, 'MX', 'MXN') returning id", conn);
        le.Parameters.AddWithValue("n", name + Guid.NewGuid().ToString("N")[..6]);
        var entity = (Guid)(await le.ExecuteScalarAsync())!;
        await using var cmd = new NpgsqlCommand(
            """
            insert into bank_account (legal_entity_id, bank_name, account_name, account_last4, currency, kind,
                                      opening_balance, opening_balance_date, feed_provider, feed_status)
            values (@e, 'Banco Test', @n, '1234', @c, 'operating', @o, '2026-01-01', 'dummy', 'connected') returning id
            """, conn);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("c", currency);
        cmd.Parameters.AddWithValue("o", opening);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task AddTxnAsync(NpgsqlConnection conn, Guid account, string date, string direction, decimal amount,
        string currency, string category)
    {
        await using var cmd = new NpgsqlCommand(
            """
            insert into financial_transaction (bank_account_id, booked_on, direction, amount, currency, category, description, source)
            values (@a, @d, @dir, @amt, @c, @cat, 'test', 'manual')
            """, conn);
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("d", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("dir", direction);
        cmd.Parameters.AddWithValue("amt", amount);
        cmd.Parameters.AddWithValue("c", currency);
        cmd.Parameters.AddWithValue("cat", category);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AddConversionAsync(NpgsqlConnection conn, Guid usd, Guid mxn, string date, decimal usdAmount,
        decimal mxnAmount, decimal rate)
    {
        var id = Guid.NewGuid();
        foreach (var (account, direction, amount, currency, counter, counterCurrency) in new[]
                 {
                     (usd, "expense", usdAmount, "USD", mxnAmount, "MXN"),
                     (mxn, "income", mxnAmount, "MXN", usdAmount, "USD"),
                 })
        {
            await using var cmd = new NpgsqlCommand(
                """
                insert into financial_transaction (bank_account_id, booked_on, direction, amount, currency, category,
                    description, source, conversion_id, counter_amount, counter_currency, fx_rate, fx_rate_source, fx_rate_at)
                values (@a, @d, @dir, @amt, @c, 'fx_conversion', 'conversion', 'manual', @id, @ca, @cc, @r, 'test bank', now())
                """, conn);
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("d", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("dir", direction);
            cmd.Parameters.AddWithValue("amt", amount);
            cmd.Parameters.AddWithValue("c", currency);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("ca", counter);
            cmd.Parameters.AddWithValue("cc", counterCurrency);
            cmd.Parameters.AddWithValue("r", rate);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
