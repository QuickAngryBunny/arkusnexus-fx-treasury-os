using Dapper;
using FxOs.Platform.Alerts;
using FxOs.Platform.Audit;
using FxOs.Platform.Tenancy;
using Npgsql;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Rate alerts (Phase 4, P2-04): each person's own alerts on the Banxico FIX. Every route acts only on the signed-in
/// person's alerts in this company; another person's alert answers 404. Checks and emails are
/// <see cref="RateAlertChecker"/>'s.
/// </summary>
public static class AlertEndpoints
{
    public const int MaxAlertsPerPerson = 20;
    private const string Source = RateAlertChecker.Source;

    public static RouteGroupBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant/alerts").WithTags("Rate alerts");

        // The drawer: today's FIX, the last 12 months, the forecast horizons the chance is read from, and your alerts.
        group.MapGet("", async (ITenantConnectionFactory db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (tenant.Principal is not { } who) return SignInRequired();
            await using var conn = await db.OpenAsync(ct);
            var history = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                """
                select rate_date, rate from fx_rate
                where source_code = @Source and rate_date > (select max(rate_date) from fx_rate where source_code = @Source) - 365
                order by rate_date
                """,
                new { Source }, cancellationToken: ct))).ToList();
            var forecast = await StoredForecasts.PrimaryAsync(conn, Source, ct);
            var alerts = (await conn.QueryAsync<AlertRow>(new CommandDefinition(
                """
                select id, direction, rate, early_warning, look_ahead_days, keep_on, is_on, reached_at, created_at
                from rate_alert where user_id = @UserId and deleted_at is null order by created_at, id
                """,
                new { who.UserId }, cancellationToken: ct))).ToList();
            var notifications = (await conn.QueryAsync<(string Code, bool Enabled)>(new CommandDefinition(
                """
                select t.code, coalesce(p.email_enabled, t.default_enabled)
                from notification_type t
                left join notification_preference p on p.notification_code = t.code and p.user_id = @UserId
                where t.code in ('rate_target_reached', 'rate_early_warning')
                """,
                new { who.UserId }, cancellationToken: ct))).ToDictionary(r => r.Code, r => r.Enabled);

            var today = history.Count > 0 ? history[^1] : default;
            double? Chance(string direction, decimal rate, int days) => forecast is null || history.Count == 0
                ? null
                : RateAlertMath.ChanceWithin(direction, rate, forecast.LastRate, forecast.Horizons, days);

            return Results.Ok(new
            {
                today = history.Count > 0 ? new { date = DateOnly.FromDateTime(today.Date), rate = today.Rate } : null,
                history = history.Select(h => new { date = DateOnly.FromDateTime(h.Date), rate = h.Rate }),
                forecast = forecast is null ? null : new
                {
                    runId = forecast.RunId,
                    model = forecast.ModelCode,
                    asOf = forecast.AsOf,
                    lastRate = forecast.LastRate,
                    horizons = forecast.Horizons.Where(h => h.Days <= 30),
                },
                trendRules = RateAlertMath.LookAheads.Select(d => new { lookAheadDays = d, needed = RateAlertMath.TrendRule(d).Needed, window = RateAlertMath.TrendRule(d).Window }),
                emails = new
                {
                    reached = notifications.GetValueOrDefault("rate_target_reached", true),
                    earlyWarning = notifications.GetValueOrDefault("rate_early_warning", true),
                },
                maxAlerts = MaxAlertsPerPerson,
                alerts = alerts.Select(a => new
                {
                    a.Id,
                    a.Direction,
                    a.Rate,
                    a.EarlyWarning,
                    a.LookAheadDays,
                    a.KeepOn,
                    a.IsOn,
                    a.ReachedAt,
                    a.CreatedAt,
                    chance30 = Chance(a.Direction, a.Rate, 30),
                    chanceLookAhead = Chance(a.Direction, a.Rate, a.LookAheadDays),
                }),
            });
        });

        group.MapPost("", async (AlertBody body, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit, CancellationToken ct) =>
        {
            if (tenant.Principal is not { } who) return SignInRequired();
            if (Validate(body) is { } invalid) return invalid;

            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            // Serialises one person's creates so the limit holds.
            await conn.ExecuteAsync(new CommandDefinition("select pg_advisory_xact_lock(hashtext('rate_alert:' || @u))",
                new { u = who.UserId.ToString("D") }, tx, cancellationToken: ct));
            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*) from rate_alert where user_id = @UserId and deleted_at is null", new { who.UserId }, tx, cancellationToken: ct));
            if (count >= MaxAlertsPerPerson)
            {
                return Results.Json(new { code = "too_many_alerts", max = MaxAlertsPerPerson }, statusCode: StatusCodes.Status409Conflict);
            }

            // Today's FIX counts as seen: the alert emails from the next FIX on. The early warning is checked on the next
            // pass (checked_run_id stays empty).
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                insert into rate_alert (user_id, source_code, direction, rate, early_warning, look_ahead_days, keep_on, checked_fix_date)
                values (@UserId, @Source, @Direction, @Rate, @EarlyWarning, @LookAheadDays, @KeepOn,
                        (select max(rate_date) from fx_rate where source_code = @Source))
                returning id
                """,
                new { who.UserId, Source, body.Direction, Rate = body.Rate!.Value, EarlyWarning = body.EarlyWarning ?? true, LookAheadDays = body.LookAheadDays ?? 7, KeepOn = body.KeepOn ?? false },
                tx, cancellationToken: ct));
            await audit.WriteAsync(conn, tx, new AuditEntry("rate_alert.created", "rate_alert", id.ToString(), Settings(body, true)), ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/tenant/alerts/{id}", new { id });
        });

        // Edit, or turn back on: any change starts the alert afresh (armed, no early warning sent yet).
        group.MapPut("/{id:long}", async (long id, AlertBody body, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit, CancellationToken ct) =>
        {
            if (tenant.Principal is not { } who) return SignInRequired();
            if (Validate(body) is { } invalid) return invalid;

            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var isOn = body.IsOn ?? true;
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                update rate_alert
                set direction = @Direction, rate = @Rate, early_warning = @EarlyWarning, look_ahead_days = @LookAheadDays,
                    keep_on = @KeepOn, is_on = @isOn, armed = true, warned = false,
                    checked_fix_date = (select max(rate_date) from fx_rate where source_code = @Source), checked_run_id = null,
                    updated_at = now()
                where id = @id and user_id = @UserId and deleted_at is null
                """,
                new { id, who.UserId, Source, body.Direction, Rate = body.Rate!.Value, EarlyWarning = body.EarlyWarning ?? true, LookAheadDays = body.LookAheadDays ?? 7, KeepOn = body.KeepOn ?? false, isOn },
                tx, cancellationToken: ct));
            if (updated == 0) return NotFound();
            await audit.WriteAsync(conn, tx, new AuditEntry("rate_alert.updated", "rate_alert", id.ToString(), Settings(body, isOn)), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:long}", async (long id, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit, CancellationToken ct) =>
        {
            if (tenant.Principal is not { } who) return SignInRequired();
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                "update rate_alert set deleted_at = now(), is_on = false, updated_at = now() where id = @id and user_id = @UserId and deleted_at is null",
                new { id, who.UserId }, tx, cancellationToken: ct));
            if (deleted == 0) return NotFound();
            await audit.WriteAsync(conn, tx, new AuditEntry("rate_alert.deleted", "rate_alert", id.ToString()), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        return group;
    }

    private static IResult? Validate(AlertBody body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AlertDirection.IsValid(body.Direction)) errors["direction"] = ["above or below."];
        if (body.Rate is not { } rate || rate is < 1 or > 100 || decimal.Round(rate, 4) != rate) errors["rate"] = ["A rate between 1 and 100, up to 4 decimals."];
        if (body.LookAheadDays is { } days && !RateAlertMath.LookAheads.Contains(days)) errors["lookAheadDays"] = ["7, 15 or 30."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static object Settings(AlertBody b, bool isOn) =>
        new { b.Direction, b.Rate, earlyWarning = b.EarlyWarning ?? true, lookAheadDays = b.LookAheadDays ?? 7, keepOn = b.KeepOn ?? false, isOn };

    private static IResult SignInRequired() => Results.Json(new { code = "sign_in_required" }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFound() => Results.Json(new { code = "not_found" }, statusCode: StatusCodes.Status404NotFound);

    private sealed class AlertRow
    {
        public long Id { get; set; }
        public string Direction { get; set; } = "";
        public decimal Rate { get; set; }
        public bool EarlyWarning { get; set; }
        public int LookAheadDays { get; set; }
        public bool KeepOn { get; set; }
        public bool IsOn { get; set; }
        public DateTime? ReachedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}

public sealed record AlertBody(string? Direction, decimal? Rate, bool? EarlyWarning, int? LookAheadDays, bool? KeepOn, bool? IsOn);
