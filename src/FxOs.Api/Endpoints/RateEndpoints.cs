using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Internal exchange-rate API. Reads only the tenant's own rate history; never calls an external source
/// (the ingestion worker loads it), so history stays readable with every connector switched off.
/// </summary>
public static class RateEndpoints
{
    /// <summary>Largest range one history request may return (the full FIX history is about 8,800 days).</summary>
    public const int MaxDays = 50 * 366;

    public static RouteGroupBuilder MapRateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant/rates").WithTags("Rates");

        group.MapGet("/sources", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<SourceRow>(new CommandDefinition(
                """
                select s.code, s.name, s.provider, s.series_id, s.base_currency, s.quote_currency, s.is_enabled,
                       r.first_date, r.last_date, coalesce(r.points, 0) as points,
                       l.status as last_load_status, l.finished_at as last_load_at, l.error as last_load_error
                from rate_source s
                left join lateral (
                    select min(rate_date) as first_date, max(rate_date) as last_date, count(*) as points
                    from fx_rate where source_code = s.code
                ) r on true
                left join lateral (
                    select status, finished_at, error from rate_load
                    where source_code = s.code order by finished_at desc limit 1
                ) l on true
                order by s.code
                """,
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new
            {
                r.Code,
                r.Name,
                r.Provider,
                r.SeriesId,
                r.BaseCurrency,
                r.QuoteCurrency,
                r.IsEnabled,
                FirstDate = ToDate(r.FirstDate),
                LastDate = ToDate(r.LastDate),
                r.Points,
                LastLoad = r.LastLoadStatus is null
                    ? null
                    : new { Status = r.LastLoadStatus, At = r.LastLoadAt, Error = r.LastLoadError },
            }));
        });

        group.MapGet("/{source}/latest", async (string source, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var info = await FindSourceAsync(conn, source, ct);
            if (info is null)
            {
                return UnknownSource(source);
            }

            var last = (await conn.QueryAsync<PointRow>(new CommandDefinition(
                """
                select rate_date, rate, revision, loaded_at from fx_rate
                where source_code = @source order by rate_date desc limit 2
                """,
                new { source },
                cancellationToken: ct))).ToList();

            if (last.Count == 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No rates yet",
                    detail: $"Source '{source}' has no rates loaded for this tenant.");
            }

            var current = last[0];
            var previous = last.Count > 1 ? last[1] : null;
            return Results.Ok(new
            {
                Source = source,
                info.BaseCurrency,
                info.QuoteCurrency,
                Date = ToDate(current.RateDate),
                current.Rate,
                current.Revision,
                current.LoadedAt,
                Previous = previous is null ? null : new { Date = ToDate(previous.RateDate), previous.Rate },
                Change = previous is null ? (decimal?)null : current.Rate - previous.Rate,
                ChangePct = previous is null ? (decimal?)null : Math.Round((current.Rate - previous.Rate) / previous.Rate * 100m, 4),
            });
        });

        group.MapGet("/{source}", async (
            string source, DateOnly? from, DateOnly? to, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var info = await FindSourceAsync(conn, source, ct);
            if (info is null)
            {
                return UnknownSource(source);
            }

            var end = to ?? ToDate(await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "select max(rate_date) from fx_rate where source_code = @source", new { source }, cancellationToken: ct)))
                ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var start = from ?? end.AddDays(-90);

            if (start > end || end.DayNumber - start.DayNumber > MaxDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["from"] = [$"'from' must be on or before 'to', at most {MaxDays} days apart."],
                });
            }

            var points = await conn.QueryAsync<PointRow>(new CommandDefinition(
                """
                select rate_date, rate, revision, loaded_at from fx_rate
                where source_code = @source and rate_date between @start and @end
                order by rate_date
                """,
                new { source, start = start.ToDateTime(TimeOnly.MinValue), end = end.ToDateTime(TimeOnly.MinValue) },
                cancellationToken: ct));

            return Results.Ok(new
            {
                Source = source,
                info.BaseCurrency,
                info.QuoteCurrency,
                From = start,
                To = end,
                Points = points.Select(p => new { Date = ToDate(p.RateDate), p.Rate }),
            });
        });

        return group;
    }

    private static Task<SourceInfo?> FindSourceAsync(Npgsql.NpgsqlConnection conn, string source, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<SourceInfo?>(new CommandDefinition(
            "select base_currency, quote_currency from rate_source where code = @source",
            new { source },
            cancellationToken: ct));

    private static IResult UnknownSource(string source) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown rate source",
            detail: $"This tenant has no rate source '{source}'.");

    private static DateOnly? ToDate(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static DateOnly ToDate(DateTime value) => DateOnly.FromDateTime(value);
}

internal sealed class SourceInfo
{
    public string BaseCurrency { get; set; } = "";
    public string QuoteCurrency { get; set; } = "";
}

internal sealed class SourceRow
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string SeriesId { get; set; } = "";
    public string BaseCurrency { get; set; } = "";
    public string QuoteCurrency { get; set; } = "";
    public bool IsEnabled { get; set; }
    public DateTime? FirstDate { get; set; }
    public DateTime? LastDate { get; set; }
    public long Points { get; set; }
    public string? LastLoadStatus { get; set; }
    public DateTime? LastLoadAt { get; set; }
    public string? LastLoadError { get; set; }
}

internal sealed class PointRow
{
    public DateTime RateDate { get; set; }
    public decimal Rate { get; set; }
    public int Revision { get; set; }
    public DateTime LoadedAt { get; set; }
}
