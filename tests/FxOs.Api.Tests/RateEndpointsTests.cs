using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 1: each tenant reads only its own copy of the rate history.</summary>
[Collection(PlatformCollection.Name)]
public sealed class RateEndpointsTests(PlatformFixture fx)
{
    [Fact]
    public async Task Each_tenant_reads_only_its_own_rate_history()
    {
        var a = await fx.ProvisionAsync("rate");
        var b = await fx.ProvisionAsync("rate");
        await InsertRatesAsync(a, ("2026-11-09", 18.70m), ("2026-11-10", 18.6678m));
        await InsertRatesAsync(b, ("2026-11-10", 99.0m));
        var client = fx.Client();

        var latestA = await GetJsonAsync(client, "/api/tenant/rates/banxico_fix/latest", a.Slug);
        var latestB = await GetJsonAsync(client, "/api/tenant/rates/banxico_fix/latest", b.Slug);

        Assert.Equal(18.6678m, latestA.GetProperty("rate").GetDecimal());
        Assert.Equal("2026-11-10", latestA.GetProperty("date").GetString());
        Assert.Equal("USD", latestA.GetProperty("baseCurrency").GetString());
        Assert.Equal("MXN", latestA.GetProperty("quoteCurrency").GetString());
        Assert.Equal(18.70m, latestA.GetProperty("previous").GetProperty("rate").GetDecimal());
        Assert.Equal(-0.0322m, latestA.GetProperty("change").GetDecimal());
        Assert.Equal(99.0m, latestB.GetProperty("rate").GetDecimal());

        var historyA = await GetJsonAsync(client, "/api/tenant/rates/banxico_fix?from=2026-01-01&to=2026-12-31", a.Slug);
        var historyB = await GetJsonAsync(client, "/api/tenant/rates/banxico_fix?from=2026-01-01&to=2026-12-31", b.Slug);
        Assert.Equal(new[] { 18.70m, 18.6678m }, historyA.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("rate").GetDecimal()));
        Assert.Equal(new[] { 99.0m }, historyB.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("rate").GetDecimal()));
    }

    [Fact]
    public async Task History_defaults_to_90_days_ending_at_the_latest_rate()
    {
        var a = await fx.ProvisionAsync("rate");
        await InsertRatesAsync(a, ("2026-01-02", 17.0m), ("2026-10-01", 18.0m), ("2026-11-10", 18.5m));

        var history = await GetJsonAsync(fx.Client(), "/api/tenant/rates/banxico_fix", a.Slug);

        Assert.Equal("2026-08-12", history.GetProperty("from").GetString());
        Assert.Equal("2026-11-10", history.GetProperty("to").GetString());
        Assert.Equal(2, history.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Sources_report_coverage_and_empty_or_unknown_sources_are_404()
    {
        var a = await fx.ProvisionAsync("rate");
        var client = fx.Client();

        var latest = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/rates/banxico_fix/latest", a.Slug));
        var unknown = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/rates/nope/latest", a.Slug));
        var badRange = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, "/api/tenant/rates/banxico_fix?from=2026-02-01&to=2026-01-01", a.Slug));
        Assert.Equal(HttpStatusCode.NotFound, latest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);

        await InsertRatesAsync(a, ("1991-11-12", 3.0735m), ("2026-11-10", 18.6678m));
        var sources = await GetJsonAsync(client, "/api/tenant/rates/sources", a.Slug);
        var fix = sources.EnumerateArray().Single(s => s.GetProperty("code").GetString() == "banxico_fix");
        Assert.Equal("SF43718", fix.GetProperty("seriesId").GetString());
        Assert.Equal("1991-11-12", fix.GetProperty("firstDate").GetString());
        Assert.Equal("2026-11-10", fix.GetProperty("lastDate").GetString());
        Assert.Equal(2, fix.GetProperty("points").GetInt64());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, string tenant)
    {
        var response = await client.SendAsync(PlatformFixture.Request(HttpMethod.Get, url, tenant));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Writes rates the way the ingestion worker does: as the tenant's own login, into its own database.</summary>
    private async Task InsertRatesAsync(TenantRecord tenant, params (string Date, decimal Rate)[] rows)
    {
        var password = await fx.Services.GetRequiredService<ISecretStore>().GetAsync(tenant.SecretRef!);
        var cs = fx.Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(TenantServerRegistry.DefaultKey, tenant.DatabaseName!, tenant.LoginRole!, password!);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var (date, rate) in rows)
        {
            await using var cmd = new NpgsqlCommand(
                "insert into fx_rate (source_code, rate_date, rate, raw_ref) values ('banxico_fix', @d, @r, 'test')", conn);
            cmd.Parameters.AddWithValue("d", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("r", rate);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
