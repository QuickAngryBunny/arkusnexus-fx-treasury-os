using System.Globalization;
using Dapper;
using FxOs.Api.Rates;
using FxOs.Platform.Audit;
using FxOs.Platform.Tenancy;
using Npgsql;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Full history (R-02): coverage and issues, year/month/day views, gap resolutions and manual sync requests.
/// Reads only the tenant's own rate copy. "Sync now" records a request; the ingestion worker does the fetching.
/// </summary>
public static class RateHistoryEndpoints
{
    public static void MapRateHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenant/rates/{source}").WithTags("Rate history");

        g.MapGet("/history/summary", async (string source, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            var rates = await LoadRatesAsync(conn, source, null, null, ct);
            var resolved = await LoadResolutionsAsync(conn, source, ct);
            var gaps = rates.Count == 0 ? 0 : MexicanBankCalendar.BusinessDays(rates[0].Date, rates[^1].Date)
                .Count(d => !rates.ContainsDate(d) && !resolved.ContainsKey(d));

            var lastLoad = await conn.QuerySingleOrDefaultAsync<LoadRow?>(new CommandDefinition(
                "select status, finished_at as at, error from rate_load where source_code = @source order by finished_at desc limit 1",
                new { source }, cancellationToken: ct));
            var request = await conn.QuerySingleOrDefaultAsync<RequestRow?>(new CommandDefinition(
                """
                select id, status, requested_at, finished_at, result from sync_request
                where source_code = @source order by requested_at desc limit 1
                """,
                new { source }, cancellationToken: ct));

            return Results.Ok(new
            {
                Source = source,
                FirstDate = rates.Count > 0 ? rates[0].Date : (DateOnly?)null,
                LastDate = rates.Count > 0 ? rates[^1].Date : (DateOnly?)null,
                Observations = rates.Count,
                OpenGaps = gaps,
                // fx_rate's primary key (source, date) makes duplicate rows impossible; reported for the R-02 card.
                Duplicates = 0,
                Corrections = rates.Sum(r => r.Revision - 1),
                HolidaysMarked = resolved.Count,
                LastLoad = lastLoad,
                LastRequest = request,
            });
        });

        g.MapGet("/history/year/{year:int}", async (string source, int year, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            var from = new DateOnly(year, 1, 1);
            var to = new DateOnly(year, 12, 31);
            var rates = await LoadRatesAsync(conn, source, from, to, ct);
            var bounds = await BoundsAsync(conn, source, ct);
            var resolved = await LoadResolutionsAsync(conn, source, ct);

            var months = Enumerable.Range(1, 12).Select(m =>
            {
                var inMonth = rates.Where(r => r.Date.Month == m).ToList();
                var gaps = GapsIn(new DateOnly(year, m, 1), new DateOnly(year, m, DateTime.DaysInMonth(year, m)), bounds, rates, resolved);
                return new
                {
                    Month = $"{year:D4}-{m:D2}",
                    Days = inMonth.Count,
                    Average = inMonth.Count > 0 ? Math.Round(inMonth.Average(r => r.Rate), 6) : (decimal?)null,
                    Low = inMonth.Count > 0 ? inMonth.Min(r => r.Rate) : (decimal?)null,
                    High = inMonth.Count > 0 ? inMonth.Max(r => r.Rate) : (decimal?)null,
                    Revised = inMonth.Count(r => r.Revision > 1),
                    Gaps = gaps,
                };
            }).Where(m => m.Days > 0 || m.Gaps > 0).OrderByDescending(m => m.Month);

            return Results.Ok(new { Source = source, Year = year, Months = months });
        });

        g.MapGet("/history/month/{year:int}/{month:int}", async (string source, int year, int month, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            if (month is < 1 or > 12)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["month"] = ["Month must be 1 to 12."] });
            }

            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            var from = new DateOnly(year, month, 1);
            var to = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            var rates = await LoadRatesAsync(conn, source, from, to, ct);
            var bounds = await BoundsAsync(conn, source, ct);
            var resolved = await LoadResolutionsAsync(conn, source, ct);

            var rows = new List<object>();
            for (var d = to; d >= from; d = d.AddDays(-1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || bounds is null || d < bounds.Value.First || d > bounds.Value.Last)
                {
                    continue;
                }

                var r = rates.Find(d);
                if (r is not null)
                {
                    rows.Add(new { Date = d, r.Rate, r.Revision, r.LoadedAt, Status = r.Revision > 1 ? "revised" : "ok" });
                }
                else if (MexicanBankCalendar.IsHoliday(d))
                {
                    rows.Add(new { Date = d, Rate = (decimal?)null, Revision = 0, LoadedAt = (DateTime?)null, Status = "holiday" });
                }
                else
                {
                    rows.Add(new { Date = d, Rate = (decimal?)null, Revision = 0, LoadedAt = (DateTime?)null, Status = resolved.ContainsKey(d) ? "holiday_marked" : "gap" });
                }
            }

            return Results.Ok(new { Source = source, Month = $"{year:D4}-{month:D2}", Days = rows });
        });

        g.MapGet("/history/day/{date}", async (string source, DateOnly date, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            var info = await conn.QuerySingleAsync<SourceInfoRow>(new CommandDefinition(
                "select provider, series_id, base_currency, quote_currency from rate_source where code = @source",
                new { source }, cancellationToken: ct));
            var row = await conn.QuerySingleOrDefaultAsync<DayRow?>(new CommandDefinition(
                "select rate_date, rate, revision, loaded_at, raw_ref from fx_rate where source_code = @source and rate_date = @d",
                new { source, d = date.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            var previous = await conn.QuerySingleOrDefaultAsync<DayRow?>(new CommandDefinition(
                """
                select rate_date, rate, revision, loaded_at, raw_ref from fx_rate
                where source_code = @source and rate_date < @d order by rate_date desc limit 1
                """,
                new { source, d = date.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            var resolution = await conn.QuerySingleOrDefaultAsync<ResolutionRow?>(new CommandDefinition(
                "select resolution, note, resolved_by, resolved_at from rate_gap_resolution where source_code = @source and rate_date = @d",
                new { source, d = date.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));

            // Every value this date has had, from the loader's audit entries (oldest first).
            var versions = (await conn.QueryAsync<VersionRow>(new CommandDefinition(
                """
                select a.occurred_at as at, a.actor, v->>'old' as old_rate, v->>'new' as new_rate, a.data->>'raw_ref' as raw_ref
                from audit_log a, jsonb_array_elements(coalesce(a.data->'revisions', '[]'::jsonb)) v
                where a.action = 'rates.loaded' and a.entity_id = @source and v->>'date' = @d
                order by a.id
                """,
                new { source, d = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken: ct))).ToList();

            var status = row is not null ? (row.Revision > 1 ? "revised" : "ok")
                : MexicanBankCalendar.IsHoliday(date) ? "holiday"
                : resolution is not null ? "holiday_marked"
                : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "weekend"
                : "gap";

            return Results.Ok(new
            {
                Source = source,
                info.Provider,
                info.SeriesId,
                info.BaseCurrency,
                info.QuoteCurrency,
                Date = date,
                Status = status,
                Rate = row?.Rate,
                Revision = row?.Revision,
                LoadedAt = row?.LoadedAt,
                RawRef = row?.RawRef,
                Previous = previous is null ? null : new { Date = DateOnly.FromDateTime(previous.RateDate), previous.Rate },
                Change = row is null || previous is null ? (decimal?)null : row.Rate - previous.Rate,
                ChangePct = row is null || previous is null ? (decimal?)null : Math.Round((row.Rate - previous.Rate) / previous.Rate * 100m, 4),
                Resolution = resolution,
                Versions = versions,
            });
        });

        g.MapPost("/gaps/{date}/holiday", async (
            string source, DateOnly date, GapResolutionRequest? body, ITenantConnectionFactory db, ITenantContext tenant,
            IAuditWriter audit, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            var bounds = await BoundsAsync(conn, source, ct);
            var hasRate = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from fx_rate where source_code = @source and rate_date = @d)",
                new { source, d = date.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            if (hasRate || bounds is null || date < bounds.Value.First || date > bounds.Value.Last || !MexicanBankCalendar.IsBusinessDay(date))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not a gap",
                    detail: $"{date:yyyy-MM-dd} is not a missing business day for '{source}'.");
            }

            var note = body?.Note?.Trim();
            await using var tx = await conn.BeginTransactionAsync(ct);
            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into rate_gap_resolution (source_code, rate_date, resolution, note, resolved_by)
                values (@source, @d, 'holiday', @note, @actor) on conflict do nothing
                """,
                new { source, d = date.ToDateTime(TimeOnly.MinValue), note = string.IsNullOrEmpty(note) ? null : note, actor = tenant.Actor },
                tx, cancellationToken: ct));
            if (inserted == 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already resolved",
                    detail: $"{date:yyyy-MM-dd} is already marked as a holiday.");
            }

            await audit.WriteAsync(conn, tx, new AuditEntry("rates.gap_marked_holiday", "rate_source", source,
                new { date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), note }), ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { Source = source, Date = date, Status = "holiday_marked" });
        });

        g.MapPost("/sync-requests", async (
            string source, SyncRequestBody? body, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit,
            CancellationToken ct) =>
        {
            if (body?.From is { } f && body.To is { } t && f > t)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["'from' must be on or before 'to'."] });
            }

            await using var conn = await db.OpenAsync(ct);
            if (!await SourceExistsAsync(conn, source, ct))
            {
                return UnknownSource(source);
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            // One open request per source is enough; a second click returns the pending one.
            var pending = await conn.QuerySingleOrDefaultAsync<RequestRow?>(new CommandDefinition(
                """
                select id, status, requested_at, finished_at, result from sync_request
                where source_code = @source and status in ('pending', 'running') order by id limit 1 for update
                """,
                new { source }, tx, cancellationToken: ct));
            if (pending is not null)
            {
                await tx.CommitAsync(ct);
                return Results.Ok(pending);
            }

            var created = await conn.QuerySingleAsync<RequestRow>(new CommandDefinition(
                """
                insert into sync_request (source_code, range_from, range_to, requested_by) values (@source, @from, @to, @actor)
                returning id, status, requested_at, finished_at, result
                """,
                new { source, from = body?.From?.ToDateTime(TimeOnly.MinValue), to = body?.To?.ToDateTime(TimeOnly.MinValue), actor = tenant.Actor },
                tx, cancellationToken: ct));
            await audit.WriteAsync(conn, tx, new AuditEntry("rates.sync_requested", "rate_source", source,
                new { requestId = created.Id, from = body?.From, to = body?.To }), ct);
            await tx.CommitAsync(ct);
            return Results.Accepted($"/api/tenant/rates/{source}/history/summary", created);
        });
    }

    private static async Task<bool> SourceExistsAsync(NpgsqlConnection conn, string source, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from rate_source where code = @source)", new { source }, cancellationToken: ct));

    private static IResult UnknownSource(string source) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown rate source",
            detail: $"This tenant has no rate source '{source}'.");

    private static async Task<(DateOnly First, DateOnly Last)?> BoundsAsync(NpgsqlConnection conn, string source, CancellationToken ct)
    {
        var b = await conn.QuerySingleAsync<(DateTime? First, DateTime? Last)>(new CommandDefinition(
            "select min(rate_date), max(rate_date) from fx_rate where source_code = @source", new { source }, cancellationToken: ct));
        return b.First is null || b.Last is null ? null : (DateOnly.FromDateTime(b.First.Value), DateOnly.FromDateTime(b.Last.Value));
    }

    private static async Task<RateList> LoadRatesAsync(NpgsqlConnection conn, string source, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(DateTime Date, decimal Rate, int Revision, DateTime LoadedAt)>(new CommandDefinition(
            """
            select rate_date, rate, revision, loaded_at from fx_rate
            where source_code = @source and (@from::date is null or rate_date >= @from::date) and (@to::date is null or rate_date <= @to::date)
            order by rate_date
            """,
            new { source, from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: ct));
        return new RateList(rows.Select(r => new RatePoint(DateOnly.FromDateTime(r.Date), r.Rate, r.Revision, r.LoadedAt)));
    }

    private static async Task<Dictionary<DateOnly, string>> LoadResolutionsAsync(NpgsqlConnection conn, string source, CancellationToken ct) =>
        (await conn.QueryAsync<(DateTime Date, string Resolution)>(new CommandDefinition(
            "select rate_date, resolution from rate_gap_resolution where source_code = @source", new { source }, cancellationToken: ct)))
        .ToDictionary(r => DateOnly.FromDateTime(r.Date), r => r.Resolution);

    private static int GapsIn(DateOnly from, DateOnly to, (DateOnly First, DateOnly Last)? bounds, RateList rates,
        Dictionary<DateOnly, string> resolved)
    {
        if (bounds is null)
        {
            return 0;
        }

        var start = from < bounds.Value.First ? bounds.Value.First : from;
        var end = to > bounds.Value.Last ? bounds.Value.Last : to;
        return start > end ? 0 : MexicanBankCalendar.BusinessDays(start, end).Count(d => !rates.ContainsDate(d) && !resolved.ContainsKey(d));
    }
}

public sealed record GapResolutionRequest(string? Note);

public sealed record SyncRequestBody(DateOnly? From, DateOnly? To);

internal sealed record RatePoint(DateOnly Date, decimal Rate, int Revision, DateTime LoadedAt);

internal sealed class RateList : List<RatePoint>
{
    private readonly Dictionary<DateOnly, RatePoint> _byDate;

    public RateList(IEnumerable<RatePoint> points)
        : base(points) => _byDate = this.ToDictionary(p => p.Date);

    public bool ContainsDate(DateOnly d) => _byDate.ContainsKey(d);

    public RatePoint? Find(DateOnly d) => _byDate.GetValueOrDefault(d);
}

internal sealed class LoadRow
{
    public string Status { get; set; } = "";
    public DateTime At { get; set; }
    public string? Error { get; set; }
}

internal sealed class RequestRow
{
    public long Id { get; set; }
    public string Status { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Result { get; set; }
}

internal sealed class SourceInfoRow
{
    public string Provider { get; set; } = "";
    public string SeriesId { get; set; } = "";
    public string BaseCurrency { get; set; } = "";
    public string QuoteCurrency { get; set; } = "";
}

internal sealed class DayRow
{
    public DateTime RateDate { get; set; }
    public decimal Rate { get; set; }
    public int Revision { get; set; }
    public DateTime LoadedAt { get; set; }
    public string RawRef { get; set; } = "";
}

internal sealed class ResolutionRow
{
    public string Resolution { get; set; } = "";
    public string? Note { get; set; }
    public string ResolvedBy { get; set; } = "";
    public DateTime ResolvedAt { get; set; }
}

internal sealed class VersionRow
{
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string? OldRate { get; set; }
    public string? NewRate { get; set; }
    public string? RawRef { get; set; }
}
