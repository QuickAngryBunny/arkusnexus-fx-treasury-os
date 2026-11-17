using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Forecast vs actual for Full history (R-02): each Banxico FIX next to the forecast the headline model made for it
/// N days earlier, with the no-change guess (naive: the rate on the day the forecast was made) beside it. Only runs the
/// app published on their day (after each Banxico sync) exist; nothing is recreated for past days (tenant V0014).
/// A forecast counts for the FIX that applied on its target date: the last FIX on or before it, as in the backtest.
/// </summary>
public static class ForecastCheckEndpoints
{
    public static readonly int[] Horizons = [7, 15, 30, 60, 90, 180];
    private const int MaxDays = 400;

    public static void MapForecastCheckEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenant/rates/{source}/history/forecast-check", async (
            string source, DateOnly from, DateOnly to, string? horizons, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            var hz = (horizons ?? "7").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => int.TryParse(h, out var v) ? v : -1).Distinct().ToArray();
            if (hz.Length == 0 || hz.Any(h => !Horizons.Contains(h)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["horizons"] = ["7, 15, 30, 60, 90 or 180, comma-separated."] });
            }

            if (to < from || to.DayNumber - from.DayNumber > MaxDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = [$"From must be before to, at most {MaxDays} days apart."] });
            }

            await using var conn = await db.OpenAsync(ct);
            // The model Exchange Forecast headlines: first non-benchmark model in display order that has runs.
            var model = await conn.QueryFirstOrDefaultAsync<ModelRow>(new CommandDefinition(
                """
                select m.code, m.name from forecast_model m
                where not m.is_benchmark and exists (select 1 from forecast_run r where r.model_code = m.code and r.source_code = @source)
                order by m.display_order, m.code collate "C" limit 1
                """,
                new { source }, cancellationToken: ct));
            if (model is null)
            {
                return Results.Ok(new { Source = source, Model = (object?)null, Horizons = hz, Days = Array.Empty<object>(), Summary = Array.Empty<object>() });
            }

            var rows = (await conn.QueryAsync<CheckRow>(new CommandDefinition(
                """
                with fx as (
                    select rate_date, rate, lag(rate_date) over (order by rate_date) as prev_date
                    from fx_rate where source_code = @source and rate_date between @from::date - 10 and @to::date
                )
                select fx.rate_date as date, fx.rate as actual, h.hz as horizon,
                       f.as_of_date, f.target_date, f.point, f.lower80, f.upper80, f.last_rate
                from fx
                cross join unnest(@hz) as h(hz)
                left join lateral (
                    select r.as_of_date, p.target_date, p.point, p.lower80, p.upper80, r.last_rate
                    from forecast_point p join forecast_run r on r.id = p.run_id
                    where p.horizon_days = h.hz and r.source_code = @source and r.model_code = @model
                      and p.target_date > coalesce(fx.prev_date, fx.rate_date - 1) and p.target_date <= fx.rate_date
                    order by p.target_date desc, r.as_of_date desc, r.id desc
                    limit 1
                ) f on true
                where fx.rate_date between @from::date and @to::date
                order by fx.rate_date, h.hz
                """,
                new
                {
                    source, model = model.Code, hz,
                    from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue),
                },
                cancellationToken: ct))).ToList();

            var days = rows.GroupBy(r => r.Date).Select(g => new
            {
                Date = DateOnly.FromDateTime(g.Key),
                Actual = g.First().Actual,
                Forecasts = g.Where(r => r.Point is not null).Select(r => new
                {
                    r.Horizon,
                    AsOf = DateOnly.FromDateTime(r.AsOfDate!.Value),
                    Target = DateOnly.FromDateTime(r.TargetDate!.Value),
                    r.Point,
                    r.Lower80,
                    r.Upper80,
                    Naive = r.LastRate,
                    Error = r.Actual - r.Point!.Value,
                    NaiveError = r.Actual - r.LastRate!.Value,
                    Within80 = r.Actual >= r.Lower80 && r.Actual <= r.Upper80,
                }),
            }).ToList();

            var summary = hz.Select(h =>
            {
                var f = rows.Where(r => r.Horizon == h && r.Point is not null).ToList();
                return new
                {
                    Horizon = h,
                    N = f.Count,
                    // Average miss (MAE) of the model and of the no-change guess, and the average signed miss (bias:
                    // positive = the FIX came in above the forecast).
                    Mae = f.Count > 0 ? Math.Round(f.Average(r => Math.Abs(r.Actual - r.Point!.Value)), 6) : (decimal?)null,
                    NaiveMae = f.Count > 0 ? Math.Round(f.Average(r => Math.Abs(r.Actual - r.LastRate!.Value)), 6) : (decimal?)null,
                    Bias = f.Count > 0 ? Math.Round(f.Average(r => r.Actual - r.Point!.Value), 6) : (decimal?)null,
                    Within80 = f.Count(r => r.Actual >= r.Lower80 && r.Actual <= r.Upper80),
                };
            });

            return Results.Ok(new { Source = source, Model = model, Horizons = hz, Days = days, Summary = summary });
        }).WithTags("Rate history");
    }

    private sealed class ModelRow
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class CheckRow
    {
        public DateTime Date { get; set; }
        public decimal Actual { get; set; }
        public int Horizon { get; set; }
        public DateTime? AsOfDate { get; set; }
        public DateTime? TargetDate { get; set; }
        public decimal? Point { get; set; }
        public decimal? Lower80 { get; set; }
        public decimal? Upper80 { get; set; }
        public decimal? LastRate { get; set; }
    }
}
