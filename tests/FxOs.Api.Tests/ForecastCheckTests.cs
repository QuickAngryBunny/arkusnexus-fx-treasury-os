using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace FxOs.Api.Tests;

/// <summary>Forecast vs actual (R-02): each FIX with the forecast made N days before, the no-change guess beside it.</summary>
[Collection(PlatformCollection.Name)]
public sealed class ForecastCheckTests(PlatformFixture fx)
{
    [Fact]
    public async Task Each_fix_gets_the_forecast_made_n_days_before_and_only_in_its_own_company()
    {
        var tenant = await fx.ProvisionAsync("fchk");
        var other = await fx.ProvisionAsync("fchx");
        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            await conn.ExecuteAsync("""
                insert into fx_rate (source_code, rate_date, rate, raw_ref) values
                    ('banxico_fix', '2026-10-01', 18.00, 't'), ('banxico_fix', '2026-10-02', 18.10, 't'),
                    ('banxico_fix', '2026-10-08', 18.40, 't'), ('banxico_fix', '2026-10-09', 18.20, 't'),
                    ('banxico_fix', '2026-10-12', 18.30, 't');
                insert into forecast_model (code, version, name, description, is_benchmark, display_order) values
                    ('naive', '1', 'Naive', 'benchmark', true, 99), ('forward', '1', 'Market forward', 'parity', false, 1);
                insert into forecast_run (source_code, model_code, model_version, as_of_date, last_rate) values
                    ('banxico_fix', 'forward', '1', '2026-10-01', 18.00),
                    ('banxico_fix', 'forward', '1', '2026-10-02', 18.10),
                    ('banxico_fix', 'naive', '1', '2026-10-01', 18.00);
                -- 1 Oct + 7 = 8 Oct (a FIX day); 2 Oct + 7 = Fri 9 Oct; a 10-day target on Sun 11 Oct counts for Mon 12 Oct.
                insert into forecast_point (run_id, horizon_days, target_date, point, lower80, upper80, lower95, upper95)
                select id, 7, as_of_date + 7, 18.10, 17.90, 18.30, 17.80, 18.40 from forecast_run where model_code = 'forward';
                insert into forecast_point (run_id, horizon_days, target_date, point, lower80, upper80, lower95, upper95)
                select id, 15, date '2026-10-11', 18.25, 18.00, 18.50, 17.90, 18.60 from forecast_run
                where model_code = 'forward' and as_of_date = '2026-10-01';
                """);
        }

        var (_, _, token) = await fx.SignInAsync(tenant, "standard");
        var (_, _, outsider) = await fx.SignInAsync(other, "standard");
        var client = fx.Client();
        const string url = "/api/tenant/rates/banxico_fix/history/forecast-check?from=2026-10-01&to=2026-10-31&horizons=7,15";
        var r = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("forward", body.GetProperty("model").GetProperty("code").GetString());
        var days = body.GetProperty("days").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!);
        Assert.Equal(5, days.Count);

        var oct8 = Assert.Single(days["2026-10-08"].GetProperty("forecasts").EnumerateArray());
        Assert.Equal("2026-10-01", oct8.GetProperty("asOf").GetString());
        Assert.Equal(0.30m, oct8.GetProperty("error").GetDecimal());       // 18.40 − 18.10
        Assert.Equal(0.40m, oct8.GetProperty("naiveError").GetDecimal());  // 18.40 − 18.00
        Assert.False(oct8.GetProperty("within80").GetBoolean());

        var oct9 = Assert.Single(days["2026-10-09"].GetProperty("forecasts").EnumerateArray());
        Assert.True(oct9.GetProperty("within80").GetBoolean());

        var oct12 = Assert.Single(days["2026-10-12"].GetProperty("forecasts").EnumerateArray());
        Assert.Equal(15, oct12.GetProperty("horizon").GetInt32());
        Assert.Equal("2026-10-11", oct12.GetProperty("target").GetString());
        Assert.Empty(days["2026-10-01"].GetProperty("forecasts").EnumerateArray());

        var week = body.GetProperty("summary").EnumerateArray().First(s => s.GetProperty("horizon").GetInt32() == 7);
        Assert.Equal(2, week.GetProperty("n").GetInt32());
        Assert.Equal(0.20m, week.GetProperty("mae").GetDecimal());       // (0.30 + 0.10) / 2
        Assert.Equal(0.25m, week.GetProperty("naiveMae").GetDecimal());  // (0.40 + 0.10) / 2
        Assert.Equal(1, week.GetProperty("within80").GetInt32());

        // Another company: no rates, no forecasts, nothing of this one.
        var theirs = await (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, url, outsider))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("model").ValueKind);
        Assert.Empty(theirs.GetProperty("days").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get,
            "/api/tenant/rates/banxico_fix/history/forecast-check?from=2026-10-01&to=2026-10-31&horizons=8", token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get,
            "/api/tenant/rates/banxico_fix/history/forecast-check?from=2024-01-01&to=2026-10-31", token))).StatusCode);
    }
}
