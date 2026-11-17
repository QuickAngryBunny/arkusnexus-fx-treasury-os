using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Api.Rates;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

public sealed class MexicanBankCalendarTests
{
    [Theory]
    [InlineData("2026-01-01")] [InlineData("2026-02-02")] [InlineData("2026-03-16")] [InlineData("2026-04-02")]
    [InlineData("2026-04-03")] [InlineData("2026-05-01")] [InlineData("2026-09-16")] [InlineData("2026-11-02")]
    [InlineData("2026-11-16")] [InlineData("2025-12-12")] [InlineData("2025-12-25")] [InlineData("2024-10-01")]
    [InlineData("2005-03-21")] [InlineData("2005-02-05")] [InlineData("2000-12-01")]
    // Historical bank holidays and one-off closures (all days Banxico published no FIX).
    [InlineData("1993-12-31")] [InlineData("1999-12-31")] [InlineData("1993-10-12")] [InlineData("2000-05-05")]
    [InlineData("1994-11-01")] [InlineData("1997-09-01")] [InlineData("1994-03-24")] [InlineData("2006-03-21")]
    [InlineData("2010-09-17")]
    public void Known_bank_holidays_are_holidays(string date) =>
        Assert.True(MexicanBankCalendar.IsHoliday(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2026-11-10")] [InlineData("2026-03-21")] [InlineData("2026-11-20")] [InlineData("2025-10-01")]
    [InlineData("1997-12-31")] [InlineData("2001-05-04")] [InlineData("2026-09-01")] [InlineData("2025-05-05")]
    public void Ordinary_days_are_not(string date) =>
        Assert.False(MexicanBankCalendar.IsHoliday(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Easter_matches_published_dates()
    {
        Assert.Equal(new DateOnly(2026, 4, 5), MexicanBankCalendar.EasterSunday(2026));
        Assert.Equal(new DateOnly(2025, 4, 20), MexicanBankCalendar.EasterSunday(2025));
        Assert.Equal(new DateOnly(2000, 4, 23), MexicanBankCalendar.EasterSunday(2000));
    }
}

/// <summary>R-02 full history: views, gap fixes and sync requests stay inside their tenant and are audited.</summary>
[Collection(PlatformCollection.Name)]
public sealed class RateHistoryTests(PlatformFixture fx)
{
    private const string Base = "/api/tenant/rates/banxico_fix";

    [Fact]
    public async Task Month_view_shows_rates_holidays_and_unexplained_gaps()
    {
        var a = await fx.ProvisionAsync("hist");
        // Nov 2026: 2 Nov is a bank holiday; 4 Nov is missing with no reason.
        await InsertRatesAsync(a, ("2026-10-30", 18.80m), ("2026-11-03", 18.76m), ("2026-11-05", 18.71m), ("2026-11-06", 18.63m));

        var month = await GetAsync(a.Slug, $"{Base}/history/month/2026/11");
        var status = month.GetProperty("days").EnumerateArray()
            .ToDictionary(d => d.GetProperty("date").GetString()!, d => d.GetProperty("status").GetString());
        Assert.Equal("holiday", status["2026-11-02"]);
        Assert.Equal("gap", status["2026-11-04"]);
        Assert.Equal("ok", status["2026-11-05"]);
        Assert.False(status.ContainsKey("2026-11-09")); // after the last stored rate

        var summary = await GetAsync(a.Slug, $"{Base}/history/summary");
        Assert.Equal(1, summary.GetProperty("openGaps").GetInt32());
        Assert.Equal(4, summary.GetProperty("observations").GetInt32());
    }

    [Fact]
    public async Task Marking_a_gap_as_holiday_is_audited_and_only_affects_that_tenant()
    {
        var a = await fx.ProvisionAsync("hist");
        var b = await fx.ProvisionAsync("hist");
        foreach (var t in new[] { a, b })
        {
            await InsertRatesAsync(t, ("2026-11-03", 18.76m), ("2026-11-05", 18.71m));
        }

        var client = fx.Client();
        var mark = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/gaps/2026-11-04/holiday", a.Slug, new { note = "test" }));
        var again = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/gaps/2026-11-04/holiday", a.Slug, new { }));
        var notAGap = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/gaps/2026-11-05/holiday", a.Slug, new { }));
        Assert.Equal(HttpStatusCode.OK, mark.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notAGap.StatusCode);

        Assert.Equal(0, (await GetAsync(a.Slug, $"{Base}/history/summary")).GetProperty("openGaps").GetInt32());
        Assert.Equal(1, (await GetAsync(b.Slug, $"{Base}/history/summary")).GetProperty("openGaps").GetInt32());
        var day = await GetAsync(a.Slug, $"{Base}/history/day/2026-11-04");
        Assert.Equal("holiday_marked", day.GetProperty("status").GetString());

        var audit = await GetAsync(a.Slug, "/api/tenant/audit?limit=5");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "rates.gap_marked_holiday");
    }

    [Fact]
    public async Task Sync_now_records_one_pending_request_and_never_calls_the_source()
    {
        var a = await fx.ProvisionAsync("hist");
        var client = fx.Client();

        var first = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/sync-requests", a.Slug, new { }));
        var second = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/sync-requests", a.Slug, new { }));
        var badRange = await client.SendAsync(PlatformFixture.Request(HttpMethod.Post, $"{Base}/sync-requests", a.Slug, new { from = "2026-11-10", to = "2026-11-01" }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
        var id1 = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var id2 = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Assert.Equal(id1, id2);
        var last = (await GetAsync(a.Slug, $"{Base}/history/summary")).GetProperty("lastRequest");
        Assert.Equal("pending", last.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Year_view_aggregates_by_month_and_day_view_shows_versions()
    {
        var a = await fx.ProvisionAsync("hist");
        await InsertRatesAsync(a, ("2026-10-29", 18.60m), ("2026-10-30", 18.80m), ("2026-11-03", 18.76m));
        await using (var conn = await OpenAsync(a))
        {
            await using var cmd = new NpgsqlCommand(
                """
                update fx_rate set rate = 18.81, revision = 2 where rate_date = '2026-10-30';
                insert into audit_log (actor, action, entity_type, entity_id, data) values ('rate-ingestion', 'rates.loaded', 'rate_source',
                  'banxico_fix', '{"raw_ref":"raw/b.json","revisions":[{"date":"2026-10-30","old":"18.800000","new":"18.81"}]}');
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var year = await GetAsync(a.Slug, $"{Base}/history/year/2026");
        var oct = year.GetProperty("months").EnumerateArray().Single(m => m.GetProperty("month").GetString() == "2026-10");
        Assert.Equal(2, oct.GetProperty("days").GetInt32());
        Assert.Equal(18.81m, oct.GetProperty("high").GetDecimal());
        Assert.Equal(1, oct.GetProperty("revised").GetInt32());

        var day = await GetAsync(a.Slug, $"{Base}/history/day/2026-10-30");
        Assert.Equal("revised", day.GetProperty("status").GetString());
        var version = Assert.Single(day.GetProperty("versions").EnumerateArray());
        Assert.Equal("18.800000", version.GetProperty("oldRate").GetString());
        Assert.Equal(1, (await GetAsync(a.Slug, $"{Base}/history/summary")).GetProperty("corrections").GetInt32());
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

    private async Task InsertRatesAsync(TenantRecord tenant, params (string Date, decimal Rate)[] rows)
    {
        await using var conn = await OpenAsync(tenant);
        foreach (var (date, rate) in rows)
        {
            await using var cmd = new NpgsqlCommand(
                "insert into fx_rate (source_code, rate_date, rate, raw_ref) values ('banxico_fix', @d, @r, 't')", conn);
            cmd.Parameters.AddWithValue("d", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("r", rate);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
