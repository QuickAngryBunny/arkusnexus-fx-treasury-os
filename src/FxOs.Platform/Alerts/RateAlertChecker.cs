using System.Text.Json;
using Dapper;
using FxOs.Platform.Audit;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FxOs.Platform.Alerts;

/// <summary>
/// Checks every company's rate alerts against its newest Banxico FIX and forecast run (tenant V0012), and emails the
/// person who set each alert. An alert is only looked at when its FIX date or forecast run changed, so a replay sends
/// nothing. Events are written with the alert's new state and an audit entry in one tenant transaction; the email
/// follows (its outbox id is the event id, so a retry after a crash is not sent twice).
/// </summary>
public sealed class RateAlertChecker(
    ControlPlaneStore store, TenantDataSources tenantDbs, ControlPlaneDb controlPlane, EmailOutbox outbox,
    AppLinks links, TimeProvider time, ILogger<RateAlertChecker> log)
{
    public const string Source = "banxico_fix";
    private const string Actor = "system:rate-alerts";

    // Enough FIXes for the longest trend window (12 moves) plus a few missed days.
    private const int RecentFixes = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One pass over every active company. Returns the events created.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var created = 0;
        foreach (var tenant in (await store.ListAsync(ct)).Where(t => t.IsActive && t.DatabaseName is not null))
        {
            try
            {
                created += await CheckTenantAsync(tenant, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Could not check rate alerts for tenant {TenantId}; retrying on the next pass", tenant.Id);
            }
        }

        return created;
    }

    public async Task<int> CheckTenantAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        var source = await tenantDbs.GetAsync(tenant, ct);
        await using var conn = await source.OpenConnectionAsync(ct);

        var fixes = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                $"select rate_date, rate from (select rate_date, rate from fx_rate where source_code = @Source order by rate_date desc limit {RecentFixes}) r order by rate_date",
                new { Source }, cancellationToken: ct)))
            .Select(r => new Fix(DateOnly.FromDateTime(r.Date), r.Rate))
            .ToList();
        var created = 0;
        if (fixes.Count > 0)
        {
            var forecast = await StoredForecasts.PrimaryAsync(conn, Source, ct);
            var latest = fixes[^1].Date.ToDateTime(TimeOnly.MinValue);
            var due = (await conn.QueryAsync<long>(new CommandDefinition(
                """
                select id from rate_alert
                where deleted_at is null and is_on and source_code = @Source
                  and (checked_fix_date is distinct from @latest or checked_run_id is distinct from @runId)
                order by id
                """,
                new { Source, latest, runId = forecast?.RunId }, cancellationToken: ct))).ToList();

            foreach (var id in due)
            {
                created += await CheckAlertAsync(conn, id, fixes, forecast, ct);
            }
        }

        await SendPendingAsync(tenant, conn, ct);
        return created;
    }

    private async Task<int> CheckAlertAsync(NpgsqlConnection conn, long id, List<Fix> fixes, StoredForecast? forecast, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Another instance may be checking it right now: skip, it will be done there.
        var row = await conn.QuerySingleOrDefaultAsync<AlertRow>(new CommandDefinition(
            """
            select id, direction, rate, early_warning, look_ahead_days, keep_on, is_on, armed, warned, checked_fix_date, checked_run_id
            from rate_alert where id = @id and deleted_at is null for update skip locked
            """,
            new { id }, tx, cancellationToken: ct));
        if (row is null)
        {
            return 0;
        }

        var state = row.ToState();
        var (next, events) = RateAlertRules.Check(state, fixes, forecast);
        if (next == state)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update rate_alert set is_on = @IsOn, armed = @Armed, warned = @Warned, checked_fix_date = @CheckedFixDate,
                   checked_run_id = @CheckedRunId, reached_at = case when @reached then @now else reached_at end, updated_at = @now
            where id = @id
            """,
            new { next.IsOn, next.Armed, next.Warned, CheckedFixDate = next.CheckedFixDate?.ToDateTime(TimeOnly.MinValue), next.CheckedRunId, reached = events.Any(e => e.Kind == AlertEventKind.Reached), now, id },
            tx, cancellationToken: ct));

        var created = 0;
        foreach (var e in events)
        {
            var horizon = forecast?.Horizon(e.Kind == AlertEventKind.Reached ? 30 : state.LookAheadDays);
            var detail = JsonSerializer.Serialize(new
            {
                trendMoves = e.TrendMoves,
                trendWindow = e.TrendWindow,
                lookAheadDays = state.LookAheadDays,
                forecast = horizon,
            }, Json);
            var eventId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                """
                insert into rate_alert_event (alert_id, kind, fix_date, fix_rate, forecast_run_id, chance, detail)
                values (@id, @Kind, @FixDate, @FixRate, @RunId, @chance, cast(@detail as jsonb))
                on conflict (alert_id, kind, fix_date) do nothing
                returning id
                """,
                new { id, e.Kind, FixDate = e.FixDate.ToDateTime(TimeOnly.MinValue), e.FixRate, e.RunId, chance = e.Chance is { } c ? Math.Round((decimal)c, 4) : (decimal?)null, detail },
                tx, cancellationToken: ct));
            if (eventId is null)
            {
                continue;
            }

            created++;
            await AuditSql.WriteAsync(conn, tx, Actor, new AuditEntry(
                e.Kind == AlertEventKind.Reached ? "rate_alert.reached" : "rate_alert.early_warning", "rate_alert", id.ToString(),
                new { eventId, fixDate = e.FixDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), fixRate = e.FixRate, forecastRunId = e.RunId, chance = e.Chance, keepOn = state.KeepOn, switchedOff = !next.IsOn }), ct);
        }

        await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>Queues the email for every event not yet in the outbox (normally the ones just created).</summary>
    private async Task SendPendingAsync(TenantRecord tenant, NpgsqlConnection conn, CancellationToken ct)
    {
        var pending = (await conn.QueryAsync<PendingRow>(new CommandDefinition(
            """
            select e.id, e.kind, e.fix_date, e.fix_rate, e.chance, e.detail::text as detail,
                   a.user_id, a.direction, a.rate, a.keep_on, a.look_ahead_days,
                   coalesce(p.language, (select locale from tenant_settings where id = 1)) as language, p.number_format,
                   coalesce(np.email_enabled, t.default_enabled, true) as email_enabled
            from rate_alert_event e
            join rate_alert a on a.id = e.alert_id
            left join user_profile p on p.user_id = a.user_id
            left join notification_type t on t.code = case e.kind when 'reached' then 'rate_target_reached' else 'rate_early_warning' end
            left join notification_preference np on np.user_id = a.user_id and np.notification_code = t.code
            where e.email_status = 'pending'
            order by e.created_at
            limit 200
            """,
            cancellationToken: ct))).ToList();

        foreach (var e in pending)
        {
            // Only the person who set the alert, and only while they are an active member of this company.
            var person = await RecipientAsync(e.UserId, tenant.Id, ct);
            if (person is null || !e.EmailEnabled)
            {
                await MarkAsync(conn, e.Id, "skipped", ct);
                continue;
            }

            var locale = e.Language is "en-US" ? "en-US" : "es-MX";
            var detail = JsonSerializer.Deserialize<EventDetail>(e.Detail, Json) ?? new EventDetail();
            var forecast = detail.Forecast is { } f ? new EmailForecast(f.Days, f.Point, f.Lower80, f.Upper80) : null;
            var fixDate = DateOnly.FromDateTime(e.FixDate);
            var message = e.Kind == AlertEventKind.Reached
                ? EmailTemplates.RateAlertReached(locale, person.Value.Email, new RateAlertReachedModel(
                    tenant.Id, person.Value.Name, e.Direction, e.Rate, fixDate, e.FixRate, e.KeepOn, forecast,
                    e.NumberFormat ?? "period_decimal", links.RateAlerts))
                : EmailTemplates.RateAlertEarlyWarning(locale, person.Value.Email, new RateAlertEarlyWarningModel(
                    tenant.Id, person.Value.Name, e.Direction, e.Rate, fixDate, e.FixRate, e.KeepOn, detail.LookAheadDays ?? e.LookAheadDays,
                    detail.TrendMoves ?? 0, detail.TrendWindow ?? 0, (double)(e.Chance ?? 0), forecast,
                    e.NumberFormat ?? "period_decimal", links.RateAlerts));

            await using (var cp = await controlPlane.OpenAsync(ct))
            {
                await outbox.EnqueueAsync(cp, null, message, ct, id: e.Id);
            }

            await MarkAsync(conn, e.Id, "queued", ct);
        }
    }

    private async Task<(string Email, string Name)?> RecipientAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        await using var cp = await controlPlane.OpenAsync(ct);
        var row = await cp.QuerySingleOrDefaultAsync<(string Email, string DisplayName)?>(new CommandDefinition(
            """
            select u.email, u.display_name
            from user_account u join tenant_membership m on m.user_id = u.id and m.tenant_id = @tenantId and m.status = 'active'
            where u.id = @userId
            """,
            new { userId, tenantId }, cancellationToken: ct));
        return row is { } r ? (r.Email, r.DisplayName) : null;
    }

    private static Task MarkAsync(NpgsqlConnection conn, Guid id, string status, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "update rate_alert_event set email_status = @status where id = @id and email_status = 'pending'",
            new { id, status }, cancellationToken: ct));

    private sealed class AlertRow
    {
        public long Id { get; set; }
        public string Direction { get; set; } = "";
        public decimal Rate { get; set; }
        public bool EarlyWarning { get; set; }
        public int LookAheadDays { get; set; }
        public bool KeepOn { get; set; }
        public bool IsOn { get; set; }
        public bool Armed { get; set; }
        public bool Warned { get; set; }
        public DateTime? CheckedFixDate { get; set; }
        public long? CheckedRunId { get; set; }

        public AlertState ToState() => new(Direction, Rate, EarlyWarning, LookAheadDays, KeepOn, IsOn, Armed, Warned,
            CheckedFixDate is { } d ? DateOnly.FromDateTime(d) : null, CheckedRunId);
    }

    private sealed class PendingRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public DateTime FixDate { get; set; }
        public decimal FixRate { get; set; }
        public decimal? Chance { get; set; }
        public string Detail { get; set; } = "{}";
        public Guid UserId { get; set; }
        public string Direction { get; set; } = "";
        public decimal Rate { get; set; }
        public bool KeepOn { get; set; }
        public int LookAheadDays { get; set; }
        public string? Language { get; set; }
        public string? NumberFormat { get; set; }
        public bool EmailEnabled { get; set; }
    }

    private sealed class EventDetail
    {
        public int? TrendMoves { get; set; }
        public int? TrendWindow { get; set; }
        public int? LookAheadDays { get; set; }
        public ForecastHorizon? Forecast { get; set; }
    }
}

/// <summary>Runs <see cref="RateAlertChecker"/> every minute (<c>Platform:CheckRateAlerts</c>, on by default).</summary>
public sealed class RateAlertCheckerService(RateAlertChecker checker, ILogger<RateAlertCheckerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await checker.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Rate alert pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
