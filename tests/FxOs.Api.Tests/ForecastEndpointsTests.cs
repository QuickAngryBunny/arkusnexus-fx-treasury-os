using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 2: analytics and published forecasts are read from the tenant's own database only.</summary>
[Collection(PlatformCollection.Name)]
public sealed class ForecastEndpointsTests(PlatformFixture fx)
{
    [Fact]
    public async Task Analytics_are_computed_from_the_tenants_own_rates()
    {
        var a = await fx.ProvisionAsync("fct");
        await using (var conn = await OpenAsync(a))
        {
            // 250 business days rising 0.01 a day from 17.00.
            var day = new DateOnly(2025, 11, 10);
            for (var i = 0; i < 250; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    continue;
                }

                await ExecAsync(conn, "insert into fx_rate (source_code, rate_date, rate, raw_ref) values ('banxico_fix', @d, @r, 't')",
                    ("d", day), ("r", 17.00m + 0.01m * i));
                i++;
            }
        }

        var analytics = await GetAsync(fx.Client(), "/api/tenant/rates/banxico_fix/analytics", a.Slug);

        Assert.Equal(19.49m, analytics.GetProperty("rate").GetDecimal());
        Assert.Equal("up", analytics.GetProperty("trend").GetString());
        Assert.Equal(19.49m, analytics.GetProperty("high52w").GetProperty("rate").GetDecimal());
        var week = analytics.GetProperty("changes").EnumerateArray().First(c => c.GetProperty("days").GetInt32() == 7);
        Assert.Equal(0.05m, week.GetProperty("change").GetDecimal());
        Assert.True(analytics.GetProperty("volatility").GetProperty("days30").GetDouble() >= 0);
        Assert.True(analytics.GetProperty("streakWeeks").GetInt32() >= 40);  // rising every week
        Assert.Equal(19.48m, analytics.GetProperty("previous").GetProperty("rate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, analytics.GetProperty("rateGap").ValueKind);  // no curves loaded
    }

    [Fact]
    public async Task Each_tenant_reads_only_its_own_published_forecasts()
    {
        var a = await fx.ProvisionAsync("fct");
        var b = await fx.ProvisionAsync("fct");
        await using (var conn = await OpenAsync(a))
        {
            await ExecAsync(conn, """
                insert into forecast_model (code, version, name, description, is_benchmark, display_order) values
                    ('naive', '1', 'Naive', 'benchmark', true, 99), ('ewma_vol', '1', 'EWMA', 'vol', false, 2),
                    ('forward', '1', 'Forward', 'parity', false, 1);
                insert into forecast_run (source_code, model_code, model_version, as_of_date, last_rate) values
                    ('banxico_fix', 'naive', '1', '2026-11-10', 18.6678), ('banxico_fix', 'ewma_vol', '1', '2026-11-10', 18.6678),
                    ('banxico_fix', 'forward', '1', '2026-11-10', 18.6678);
                insert into forecast_point (run_id, horizon_days, target_date, point, lower80, upper80, lower95, upper95)
                select id, 30, '2026-12-10', case model_code when 'forward' then 18.7069 else 18.6678 end, 18.0, 19.3, 17.7, 19.7
                from forecast_run;
                insert into backtest_result (source_code, model_code, model_version, horizon_days, origins_from, origins_to,
                                             n, mae, mape, coverage80, coverage95, mae_vs_naive)
                values ('banxico_fix', 'forward', '1', 30, '2019-01-02', '2026-10-01', 392, 0.4873, 2.45, 0.8469, 0.9592, 1.042);
                """);
        }

        var client = fx.Client();
        var forecasts = await GetAsync(client, "/api/tenant/forecasts/banxico_fix", a.Slug);
        var none = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/forecasts/banxico_fix", b.Slug));

        Assert.Equal("forward", forecasts.GetProperty("primary").GetString());
        var models = forecasts.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(new[] { "forward", "ewma_vol", "naive" }, models.Select(m => m.GetProperty("code").GetString()));
        var point = Assert.Single(models[0].GetProperty("points").EnumerateArray());
        Assert.Equal(18.7069m, point.GetProperty("point").GetDecimal());
        Assert.Equal(1.042m, Assert.Single(models[0].GetProperty("backtest").EnumerateArray()).GetProperty("maeVsNaive").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
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

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
