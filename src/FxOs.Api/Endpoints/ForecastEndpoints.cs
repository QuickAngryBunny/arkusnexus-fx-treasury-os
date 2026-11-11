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
            // ~5 years of business days: 1-year changes, 52-week range, 200-day average and the volatility percentile.
            var rows = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                """
                select rate_date, rate from (
                    select rate_date, rate from fx_rate where source_code = @source order by rate_date desc limit 1300
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

            var vol30 = AnnualizedVolPct(rates, 21);
            var volHistory = RollingVol(dates, rates, 21);
            var gap = await RateGapAsync(conn, dates, rates, lastDate, ct);

            return Results.Ok(new
            {
                Source = source,
                Date = lastDate,
                Rate = last,
                Previous = rates.Length > 1 ? new { Date = dates[^2], Rate = rates[^2] } : null,
                // Consecutive weekly closes in the same direction: +3 = up three weeks in a row, -2 = down two.
                StreakWeeks = WeeklyStreak(dates, rates),
                VolatilityDetail = vol30 is null ? null : new
                {
                    Level = VolLevel(vol30.Value, volHistory.Select(v => v.Vol).ToArray()),
                    // One standard deviation of a daily move, in quote currency.
                    TypicalDailyMove = Math.Round(last * (decimal)(vol30.Value / 100 / Math.Sqrt(TradingDaysPerYear)), 4),
                    Peak12m = PeakMonth(volHistory, lastDate.AddDays(-365)),
                },
                RateGap = gap,
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

    /// <summary>Weekly closes (last rate of each ISO week); counts the latest run of moves in one direction.</summary>
    private static int WeeklyStreak(DateOnly[] dates, decimal[] rates)
    {
        var closes = dates.Select((d, i) => (Week: System.Globalization.ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue)) * 100
                                                     + System.Globalization.ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)), Rate: rates[i]))
            .GroupBy(x => x.Week).Select(g => g.Last().Rate).ToArray();
        var streak = 0;
        for (var i = closes.Length - 1; i > 0; i--)
        {
            var dir = Math.Sign(closes[i] - closes[i - 1]);
            if (dir == 0 || (streak != 0 && Math.Sign(streak) != dir))
            {
                break;
            }

            streak += dir;
        }

        return streak;
    }

    /// <summary>21-day annualized volatility at each date (from the 22nd observation on).</summary>
    private static List<(DateOnly Date, double Vol)> RollingVol(DateOnly[] dates, decimal[] rates, int n)
    {
        var rets = new double[rates.Length];
        for (var i = 1; i < rates.Length; i++)
        {
            rets[i] = Math.Log((double)rates[i] / (double)rates[i - 1]);
        }

        var result = new List<(DateOnly, double)>();
        for (var end = n; end < rates.Length; end++)
        {
            var window = rets.AsSpan(end - n + 1, n);
            double mean = 0, sq = 0;
            foreach (var r in window) { mean += r; }
            mean /= n;
            foreach (var r in window) { sq += (r - mean) * (r - mean); }
            result.Add((dates[end], Math.Sqrt(sq / (n - 1) * TradingDaysPerYear) * 100));
        }

        return result;
    }

    /// <summary>low / normal / high by where today's volatility sits in its own recent history (thirds).</summary>
    private static string VolLevel(double current, double[] history)
    {
        if (history.Length < 60)
        {
            return "normal";
        }

        var below = history.Count(v => v < current) / (double)history.Length;
        return below < 1 / 3.0 ? "low" : below > 2 / 3.0 ? "high" : "normal";
    }

    /// <summary>The month (YYYY-MM) with the highest 21-day volatility since <paramref name="since"/>.</summary>
    private static object? PeakMonth(List<(DateOnly Date, double Vol)> vols, DateOnly since)
    {
        var recent = vols.Where(v => v.Date > since).ToList();
        if (recent.Count == 0)
        {
            return null;
        }

        var peak = recent.MaxBy(v => v.Vol);
        return new { Month = peak.Date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), Vol = Math.Round(peak.Vol, 2) };
    }

    /// <summary>
    /// MXN–USD short-rate gap (TIIE 28 − US Treasury 1 month) and how USD/MXN daily changes have moved with daily changes
    /// of that gap over the last 63 business days (~90 calendar days). Null when the curves are not loaded.
    /// </summary>
    private static async Task<object?> RateGapAsync(Npgsql.NpgsqlConnection conn, DateOnly[] dates, decimal[] rates,
        DateOnly lastDate, CancellationToken ct)
    {
        var points = (await conn.QueryAsync<(string Code, DateTime Date, decimal Pct)>(new CommandDefinition(
            """
            select series_code, rate_date, rate_pct from interest_rate
            where series_code in ('tiie_28', 'ust_1m') and rate_date between @from and @to
            order by rate_date
            """,
            new { from = lastDate.AddDays(-200).ToDateTime(TimeOnly.MinValue), to = lastDate.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: ct))).ToList();

        decimal? AsOf(string code, DateOnly day) =>
            points.LastOrDefault(p => p.Code == code && DateOnly.FromDateTime(p.Date) <= day && DateOnly.FromDateTime(p.Date) >= day.AddDays(-10)) is var p
            && p.Code is not null ? p.Pct : null;

        var mxn = AsOf("tiie_28", lastDate);
        var usd = AsOf("ust_1m", lastDate);
        if (mxn is null || usd is null)
        {
            return null;
        }

        var fx = new List<double>();
        var gaps = new List<double>();
        for (var i = Math.Max(1, dates.Length - 63); i < dates.Length; i++)
        {
            var g0 = AsOf("tiie_28", dates[i - 1]) - AsOf("ust_1m", dates[i - 1]);
            var g1 = AsOf("tiie_28", dates[i]) - AsOf("ust_1m", dates[i]);
            if (g0 is null || g1 is null)
            {
                continue;
            }

            fx.Add(Math.Log((double)rates[i] / (double)rates[i - 1]));
            gaps.Add((double)(g1.Value - g0.Value));
        }

        return new
        {
            MxnSeries = "tiie_28",
            MxnRatePct = mxn,
            UsdSeries = "ust_1m",
            UsdRatePct = usd,
            GapPts = mxn - usd,
            Correlation90d = Correlation(fx, gaps),
        };
    }

    private static double? Correlation(List<double> x, List<double> y)
    {
        if (x.Count < 20)
        {
            return null;
        }

        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }

        return sxx == 0 || syy == 0 ? null : Math.Round(sxy / Math.Sqrt(sxx * syy), 2);
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
