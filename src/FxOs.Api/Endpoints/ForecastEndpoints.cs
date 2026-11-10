using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Rate analytics (computed on read from the tenant's own history) and published forecasts with their backtests
/// (computed by the forecasting worker). Deterministic statistics only; nothing here calls an external source.
/// </summary>
public static class ForecastEndpoints
{
    private const double TradingDaysPerYear = 252;

    public static void MapForecastEndpoints(this IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/tenant").WithTags("Forecasting");

        tenant.MapGet("/rates/{source}/analytics", async (string source, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            // ~14 months of business days covers the 1-year change, 52-week range and 200-day average.
            var rows = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                """
                select rate_date, rate from (
                    select rate_date, rate from fx_rate where source_code = @source order by rate_date desc limit 300
                ) recent order by rate_date
                """,
                new { source },
                cancellationToken: ct))).ToList();

            if (rows.Count < 2)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not enough rates",
                    detail: $"Source '{source}' needs at least two rates for analytics.");
            }

            var dates = rows.Select(r => DateOnly.FromDateTime(r.Date)).ToArray();
            var rates = rows.Select(r => r.Rate).ToArray();
            var last = rates[^1];
            var lastDate = dates[^1];

            object? Change(int days)
            {
                var target = lastDate.AddDays(-days);
                var i = Array.FindLastIndex(dates, d => d <= target);
                if (i < 0)
                {
                    return null;
                }

                return new { Days = days, From = dates[i], FromRate = rates[i], Change = last - rates[i], ChangePct = Pct(last, rates[i]) };
            }

            var yearAgo = lastDate.AddDays(-365);
            var window = Enumerable.Range(0, rates.Length).Where(i => dates[i] > yearAgo).ToArray();
            var hi = window.MaxBy(i => rates[i]);
            var lo = window.MinBy(i => rates[i]);
            var sma50 = Sma(rates, 50);
            var sma200 = Sma(rates, 200);
            var trend = sma50 is null || sma200 is null ? "unknown"
                : sma50 > sma200 * 1.005m ? "up"
                : sma50 < sma200 * 0.995m ? "down"
                : "flat";

            return Results.Ok(new
            {
                Source = source,
                Date = lastDate,
                Rate = last,
                Changes = new[] { Change(7), Change(30), Change(90), Change(365) }.Where(c => c is not null),
                Volatility = new { Days30 = AnnualizedVolPct(rates, 21), Days90 = AnnualizedVolPct(rates, 63) },
                High52w = new { Date = dates[hi], Rate = rates[hi] },
                Low52w = new { Date = dates[lo], Rate = rates[lo] },
                Sma50 = sma50,
                Sma200 = sma200,
                // "up" means the quote currency weakens (more MXN per USD).
                Trend = trend,
            });
        });

        tenant.MapGet("/forecasts/{source}", async (string source, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var runs = (await conn.QueryAsync<RunRow>(new CommandDefinition(
                """
                select distinct on (r.model_code) r.id, r.model_code, r.model_version, r.as_of_date, r.last_rate,
                       r.created_at, m.name, m.description, m.is_benchmark, m.display_order
                from forecast_run r
                join forecast_model m on m.code = r.model_code and m.version = r.model_version
                where r.source_code = @source
                order by r.model_code, r.as_of_date desc, r.id desc
                """,
                new { source },
                cancellationToken: ct))).ToList();

            if (runs.Count == 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No forecasts yet",
                    detail: $"No forecast has been published for '{source}'.");
            }

            var ids = runs.Select(r => r.Id).ToArray();
            var points = (await conn.QueryAsync<PointRow2>(new CommandDefinition(
                """
                select run_id, horizon_days, target_date, point, lower80, upper80, lower95, upper95
                from forecast_point where run_id = any(@ids) order by run_id, horizon_days
                """,
                new { ids },
                cancellationToken: ct))).ToLookup(p => p.RunId);

            var scores = (await conn.QueryAsync<ScoreRow>(new CommandDefinition(
                """
                select distinct on (model_code, model_version, horizon_days)
                       model_code, model_version, horizon_days, origins_from, origins_to, n, mae, mape,
                       coverage80, coverage95, mae_vs_naive
                from backtest_result where source_code = @source
                order by model_code, model_version, horizon_days, origins_to desc, id desc
                """,
                new { source },
                cancellationToken: ct))).ToLookup(s => (s.ModelCode, s.ModelVersion));

            // Models in registry display order; the headline is the first non-benchmark model. The benchmark is
            // always returned so every model's track record can be read against it.
            runs = runs.OrderBy(r => r.DisplayOrder).ThenBy(r => r.ModelCode, StringComparer.Ordinal).ToList();
            var primary = runs.FirstOrDefault(r => !r.IsBenchmark) ?? runs[0];
            return Results.Ok(new
            {
                Source = source,
                Primary = primary.ModelCode,
                Models = runs.Select(r => new
                {
                    Code = r.ModelCode,
                    Version = r.ModelVersion,
                    r.Name,
                    r.Description,
                    r.IsBenchmark,
                    AsOf = DateOnly.FromDateTime(r.AsOfDate),
                    r.LastRate,
                    PublishedAt = r.CreatedAt,
                    Points = points[r.Id].Select(p => new
                    {
                        p.HorizonDays,
                        TargetDate = DateOnly.FromDateTime(p.TargetDate),
                        p.Point,
                        p.Lower80,
                        p.Upper80,
                        p.Lower95,
                        p.Upper95,
                    }),
                    Backtest = scores[(r.ModelCode, r.ModelVersion)].OrderBy(s => s.HorizonDays).Select(s => new
                    {
                        s.HorizonDays,
                        s.N,
                        OriginsFrom = DateOnly.FromDateTime(s.OriginsFrom),
                        OriginsTo = DateOnly.FromDateTime(s.OriginsTo),
                        s.Mae,
                        s.Mape,
                        s.Coverage80,
                        s.Coverage95,
                        s.MaeVsNaive,
                    }),
                }),
            });
        });
    }

    private static decimal Pct(decimal now, decimal then) => Math.Round((now - then) / then * 100m, 4);

    private static decimal? Sma(decimal[] rates, int n) =>
        rates.Length < n ? null : Math.Round(rates[^n..].Average(), 6);

    private static double? AnnualizedVolPct(decimal[] rates, int n)
    {
        if (rates.Length < n + 1)
        {
            return null;
        }

        var rets = Enumerable.Range(rates.Length - n, n)
            .Select(i => Math.Log((double)rates[i] / (double)rates[i - 1]))
            .ToArray();
        var mean = rets.Average();
        var variance = rets.Sum(r => (r - mean) * (r - mean)) / (n - 1);
        return Math.Round(Math.Sqrt(variance * TradingDaysPerYear) * 100, 2);
    }
}

internal sealed class RunRow
{
    public long Id { get; set; }
    public string ModelCode { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public DateTime AsOfDate { get; set; }
    public decimal LastRate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBenchmark { get; set; }
    public int DisplayOrder { get; set; }
}

internal sealed class PointRow2
{
    public long RunId { get; set; }
    public int HorizonDays { get; set; }
    public DateTime TargetDate { get; set; }
    public decimal Point { get; set; }
    public decimal Lower80 { get; set; }
    public decimal Upper80 { get; set; }
    public decimal Lower95 { get; set; }
    public decimal Upper95 { get; set; }
}

internal sealed class ScoreRow
{
    public string ModelCode { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public int HorizonDays { get; set; }
    public DateTime OriginsFrom { get; set; }
    public DateTime OriginsTo { get; set; }
    public int N { get; set; }
    public decimal Mae { get; set; }
    public decimal Mape { get; set; }
    public decimal Coverage80 { get; set; }
    public decimal Coverage95 { get; set; }
    public decimal MaeVsNaive { get; set; }
}
