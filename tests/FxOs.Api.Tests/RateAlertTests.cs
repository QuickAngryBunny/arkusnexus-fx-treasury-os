using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FxOs.Platform.Alerts;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 4 exit criteria: rate alerts email once per reach or crossing, warn early once per trend, never twice on a replay, and only their creator.</summary>
[Collection(PlatformCollection.Name)]
public sealed class RateAlertTests(PlatformFixture fx)
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    [Fact]
    public async Task People_see_and_change_only_their_own_alerts_in_their_own_company()
    {
        var tenant = await fx.ProvisionAsync("ralo");
        var other = await fx.ProvisionAsync("ralx");
        var (_, _, ana) = await fx.SignInAsync(tenant, "standard");
        var (_, _, leo) = await fx.SignInAsync(tenant, "approver");
        var (_, _, outsider) = await fx.SignInAsync(other, "admin");
        await SeedFixesAsync(tenant, Enumerable.Repeat(18.40m, 20));
        var client = fx.Client();

        var id = await CreateAsync(client, ana, new { direction = "above", rate = 19.00m, earlyWarning = true, lookAheadDays = 15, keepOn = false });
        var mine = await ListAsync(client, ana);
        var alert = Assert.Single(mine.GetProperty("alerts").EnumerateArray());
        Assert.Equal(19.00m, alert.GetProperty("rate").GetDecimal());
        Assert.Equal(15, alert.GetProperty("lookAheadDays").GetInt32());
        Assert.Equal(18.40m, mine.GetProperty("today").GetProperty("rate").GetDecimal());

        // Same company, another person: not listed, not editable, not deletable.
        Assert.Empty((await ListAsync(client, leo)).GetProperty("alerts").EnumerateArray());
        var body = new { direction = "below", rate = 17.5m, earlyWarning = false, lookAheadDays = 7, keepOn = true };
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, $"/api/tenant/alerts/{id}", leo, body))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Delete, $"/api/tenant/alerts/{id}", leo))).StatusCode);
        // Another company: its own (empty) list, and the id means nothing there.
        Assert.Empty((await ListAsync(client, outsider)).GetProperty("alerts").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Delete, $"/api/tenant/alerts/{id}", outsider))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, $"/api/tenant/alerts/{id}", ana, body))).StatusCode);
        alert = Assert.Single((await ListAsync(client, ana)).GetProperty("alerts").EnumerateArray());
        Assert.Equal("below", alert.GetProperty("direction").GetString());
        Assert.True(alert.GetProperty("keepOn").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/tenant/alerts", ana,
            new { direction = "sideways", rate = 19m }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/tenant/alerts", ana,
            new { direction = "above", rate = 19m, lookAheadDays = 10 }))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Delete, $"/api/tenant/alerts/{id}", ana))).StatusCode);
        Assert.Empty((await ListAsync(client, ana)).GetProperty("alerts").EnumerateArray());

        await using var conn = await fx.OpenTenantAsync(tenant);
        var actions = (await conn.QueryAsync<string>("select action from audit_log where entity_type = 'rate_alert' order by id")).ToList();
        Assert.Equal(["rate_alert.created", "rate_alert.updated", "rate_alert.deleted"], actions);
    }

    [Fact]
    public async Task Keep_on_off_sends_one_email_when_reached_and_nothing_after()
    {
        var tenant = await fx.ProvisionAsync("ralr");
        var (_, email, token) = await fx.SignInAsync(tenant, "standard");
        await SeedFixesAsync(tenant, Enumerable.Repeat(18.40m, 20));
        var client = fx.Client();
        await CreateAsync(client, token, new { direction = "above", rate = 19.00m, earlyWarning = false, keepOn = false });

        await CheckAsync(tenant);
        Assert.Equal(0, await EmailsAsync(email));

        await AddFixesAsync(tenant, 19.03m, 19.10m);
        await CheckAsync(tenant);
        await CheckAsync(tenant);  // replay
        Assert.Equal(1, await EmailsAsync(email, "rate_alert_reached"));
        var body = await fx.EmailBodyAsync(email, "rate_alert_reached");
        Assert.Contains("USD/MXN 19.03", body);  // the first FIX past the level
        Assert.Contains("This alert is now off", body);

        await AddFixesAsync(tenant, 18.80m, 19.20m);
        await CheckAsync(tenant);
        Assert.Equal(1, await EmailsAsync(email));
        var alert = Assert.Single((await ListAsync(client, token)).GetProperty("alerts").EnumerateArray());
        Assert.False(alert.GetProperty("isOn").GetBoolean());

        await using var conn = await fx.OpenTenantAsync(tenant);
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("select count(*) from audit_log where action = 'rate_alert.reached'"));
    }

    [Fact]
    public async Task Keep_on_sends_one_email_per_crossing()
    {
        var tenant = await fx.ProvisionAsync("ralk");
        var (_, email, token) = await fx.SignInAsync(tenant, "standard");
        await SeedFixesAsync(tenant, Enumerable.Repeat(18.40m, 20));
        var client = fx.Client();
        await CreateAsync(client, token, new { direction = "below", rate = 18.00m, earlyWarning = false, keepOn = true });

        // Crosses down, stays below (no second email), moves back up, crosses down again: two emails.
        await AddFixesAsync(tenant, 17.95m, 17.90m, 18.10m);
        await CheckAsync(tenant);
        await AddFixesAsync(tenant, 17.99m);
        await CheckAsync(tenant);
        await CheckAsync(tenant);

        Assert.Equal(2, await EmailsAsync(email, "rate_alert_reached"));
        Assert.Contains("Your alert stays on", await fx.EmailBodyAsync(email, "rate_alert_reached"));
        Assert.True(Assert.Single((await ListAsync(client, token)).GetProperty("alerts").EnumerateArray()).GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task Early_warning_goes_out_once_per_trend_and_only_above_half()
    {
        var tenant = await fx.ProvisionAsync("rale");
        var (_, email, token) = await fx.SignInAsync(tenant, "standard");
        var (_, otherEmail, _) = await fx.SignInAsync(tenant, "admin");
        await SeedFixesAsync(tenant, Enumerable.Repeat(18.40m, 20));
        var client = fx.Client();
        await CreateAsync(client, token, new { direction = "above", rate = 18.80m, earlyWarning = true, lookAheadDays = 7, keepOn = false });

        // A forecast that makes 18.80 unlikely: rising days alone don't warn.
        await AddFixesAsync(tenant, 18.45m, 18.50m, 18.55m, 18.60m);
        await PublishAsync(tenant, 18.60m, point7: 18.62m, upper7: 18.70m);
        await CheckAsync(tenant);
        Assert.Equal(0, await EmailsAsync(email));

        // A new run that makes it likely: one early warning, and none on replay or on the next rising day.
        var run = await PublishAsync(tenant, 18.60m, point7: 18.85m, upper7: 19.00m);
        await CheckAsync(tenant);
        await CheckAsync(tenant);
        await AddFixesAsync(tenant, 18.65m);
        await CheckAsync(tenant);
        Assert.Equal(1, await EmailsAsync(email, "rate_alert_early_warning"));
        var body = await fx.EmailBodyAsync(email, "rate_alert_early_warning");
        Assert.Contains("of the last 5 days", body);
        Assert.Contains("within 7 days", body);

        // The trend breaks (falls on most days), then comes back: a second warning for the new trend.
        await AddFixesAsync(tenant, 18.55m, 18.50m, 18.45m, 18.40m);
        await CheckAsync(tenant);
        await AddFixesAsync(tenant, 18.45m, 18.50m, 18.55m, 18.60m);
        await CheckAsync(tenant);
        Assert.Equal(2, await EmailsAsync(email, "rate_alert_early_warning"));

        // Only to the person who set it.
        Assert.Equal(0, await EmailsAsync(otherEmail));
        await using var conn = await fx.OpenTenantAsync(tenant);
        Assert.True(await conn.ExecuteScalarAsync<long>("select count(*) from rate_alert_event where forecast_run_id = @run", new { run }) >= 1);
    }

    [Fact]
    public async Task Emails_turned_off_in_settings_are_recorded_but_not_sent()
    {
        var tenant = await fx.ProvisionAsync("raln");
        var (_, email, token) = await fx.SignInAsync(tenant, "standard");
        await SeedFixesAsync(tenant, Enumerable.Repeat(18.40m, 20));
        var client = fx.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PlatformFixture.Authed(HttpMethod.Put, "/api/tenant/me/notifications", token,
            new { items = new[] { new { code = "rate_target_reached", enabled = false } } }))).StatusCode);
        await CreateAsync(client, token, new { direction = "above", rate = 18.50m, earlyWarning = false });

        await AddFixesAsync(tenant, 18.60m);
        await CheckAsync(tenant);

        Assert.Equal(0, await EmailsAsync(email));
        await using var conn = await fx.OpenTenantAsync(tenant);
        Assert.Equal("skipped", await conn.ExecuteScalarAsync<string>("select email_status from rate_alert_event"));
    }

    // ── helpers ──

    private Task CheckAsync(TenantRecord tenant) => fx.Services.GetRequiredService<RateAlertChecker>().CheckTenantAsync(tenant);

    private static async Task<long> CreateAsync(HttpClient client, string token, object body)
    {
        var r = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Post, "/api/tenant/alerts", token, body));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string token)
    {
        var r = await client.SendAsync(PlatformFixture.Authed(HttpMethod.Get, "/api/tenant/alerts", token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> EmailsAsync(string to, string? template = null)
    {
        await using var conn = await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "select count(*) from email_outbox where to_email = @to and (@template::text is null or template = @template)",
            new { to = to.ToLowerInvariant(), template });
    }

    private async Task SeedFixesAsync(TenantRecord tenant, IEnumerable<decimal> rates)
    {
        await using var conn = await fx.OpenTenantAsync(tenant);
        // English emails for the assertions below (people without a profile get the company language).
        await conn.ExecuteAsync("update tenant_settings set locale = 'en-US' where id = 1");
        var day = Start;
        foreach (var rate in rates)
        {
            await InsertFixAsync(conn, day, rate);
            day = NextBusinessDay(day);
        }
    }

    private async Task AddFixesAsync(TenantRecord tenant, params decimal[] rates)
    {
        await using var conn = await fx.OpenTenantAsync(tenant);
        var last = DateOnly.FromDateTime(await conn.ExecuteScalarAsync<DateTime>("select max(rate_date) from fx_rate"));
        foreach (var rate in rates)
        {
            last = NextBusinessDay(last);
            await InsertFixAsync(conn, last, rate);
        }
    }

    private static Task InsertFixAsync(NpgsqlConnection conn, DateOnly day, decimal rate) =>
        conn.ExecuteAsync("insert into fx_rate (source_code, rate_date, rate, raw_ref) values ('banxico_fix', @day, @rate, 't')", new { day = day.ToDateTime(TimeOnly.MinValue), rate });

    private static DateOnly NextBusinessDay(DateOnly d)
    {
        do
        {
            d = d.AddDays(1);
        }
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return d;
    }

    /// <summary>A forecast run with a 7-day horizon (and wide 15/30-day ones); returns its id.</summary>
    private async Task<long> PublishAsync(TenantRecord tenant, decimal last, decimal point7, decimal upper7)
    {
        await using var conn = await fx.OpenTenantAsync(tenant);
        // A run is unique per (source, model, version, as-of date): each call registers a new model version.
        var version = ((await conn.ExecuteScalarAsync<int>("select count(*) from forecast_run")) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await conn.ExecuteAsync(
            "insert into forecast_model (code, version, name, description, is_benchmark, display_order) values ('forward', @version, 'Forward', 'parity', false, 1)",
            new { version });
        var asOf = DateOnly.FromDateTime(await conn.ExecuteScalarAsync<DateTime>("select max(rate_date) from fx_rate"));
        var id = await conn.ExecuteScalarAsync<long>(
            "insert into forecast_run (source_code, model_code, model_version, as_of_date, last_rate) values ('banxico_fix', 'forward', @version, @asOf, @last) returning id",
            new { version, asOf = asOf.ToDateTime(TimeOnly.MinValue), last });
        var lower7 = point7 - (upper7 - point7);
        await conn.ExecuteAsync("""
            insert into forecast_point (run_id, horizon_days, target_date, point, lower80, upper80, lower95, upper95) values
                (@id, 7, cast(@asOf as date) + 7, @point7, @lower7, @upper7, @lower7 - 0.1, @upper7 + 0.1),
                (@id, 15, cast(@asOf as date) + 15, @point7, @lower7 - 0.2, @upper7 + 0.2, @lower7 - 0.4, @upper7 + 0.4),
                (@id, 30, cast(@asOf as date) + 30, @point7, @lower7 - 0.4, @upper7 + 0.4, @lower7 - 0.8, @upper7 + 0.8)
            """, new { id, asOf = asOf.ToDateTime(TimeOnly.MinValue), point7, lower7, upper7 });
        return id;
    }
}
