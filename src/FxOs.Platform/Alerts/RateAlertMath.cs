namespace FxOs.Platform.Alerts;

/// <summary>"above" = rises to or above the rate; "below" = falls to or below it.</summary>
public static class AlertDirection
{
    public const string Above = "above";
    public const string Below = "below";

    public static bool IsValid(string? value) => value is Above or Below;
}

/// <summary>One stored forecast horizon: calendar days ahead, the central value and its 8-in-10 range.</summary>
public sealed record ForecastHorizon(int Days, decimal Point, decimal Lower80, decimal Upper80);

/// <summary>
/// The deterministic rules behind rate alerts (Phase 4). Nothing here builds a forecast: the chance is read from a stored
/// forecast run, treating each horizon's 8-in-10 range as a normal spread around its central value.
/// </summary>
public static class RateAlertMath
{
    /// <summary>z for the edge of a central 80% range.</summary>
    public const double Z80 = 1.2815515655446004;

    public static readonly int[] LookAheads = [7, 15, 30];

    /// <summary>
    /// How many of the most recent daily moves must go toward the rate before an early warning: 3 of the last 5 for a
    /// 7-day look-ahead, 5 of the last 8 for 15 days, 7 of the last 12 for 30 days.
    /// </summary>
    public static (int Needed, int Window) TrendRule(int lookAheadDays) => lookAheadDays switch
    {
        <= 7 => (3, 5),
        <= 15 => (5, 8),
        _ => (7, 12),
    };

    /// <summary>The FIX is at or past the alert's rate.</summary>
    public static bool IsPast(string direction, decimal rate, decimal fix) =>
        direction == AlertDirection.Above ? fix >= rate : fix <= rate;

    /// <summary>Of the last <paramref name="window"/> daily moves in <paramref name="fixes"/> (oldest first), how many went toward the rate.</summary>
    public static int MovesToward(string direction, IReadOnlyList<decimal> fixes, int window)
    {
        var count = 0;
        for (var i = Math.Max(1, fixes.Count - window); i < fixes.Count; i++)
        {
            var move = fixes[i] - fixes[i - 1];
            if (direction == AlertDirection.Above ? move > 0 : move < 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether the FIX trend meets <see cref="TrendRule"/> for the look-ahead.</summary>
    public static bool TrendHolds(string direction, IReadOnlyList<decimal> fixes, int lookAheadDays)
    {
        var (needed, window) = TrendRule(lookAheadDays);
        return fixes.Count > window && MovesToward(direction, fixes, window) >= needed;
    }

    /// <summary>
    /// The chance (0–1) that the rate is at or past <paramref name="rate"/> on some day within <paramref name="days"/>
    /// calendar days: the highest single-day chance along the forecast's path. The central value is interpolated
    /// linearly from today's rate through the stored horizons; the spread grows with the variance, from zero today,
    /// using the side of each range that faces the rate.
    /// </summary>
    public static double ChanceWithin(string direction, decimal rate, decimal lastRate, IReadOnlyList<ForecastHorizon> horizons, int days)
    {
        if (IsPast(direction, rate, lastRate))
        {
            return 1;
        }

        var points = horizons.Where(h => h.Days > 0).OrderBy(h => h.Days).ToList();
        if (points.Count == 0 || days <= 0)
        {
            return 0;
        }

        var above = direction == AlertDirection.Above;
        var best = 0.0;
        for (var k = 1; k <= days; k++)
        {
            var (mean, sd) = At(points, (double)lastRate, k, above);
            var z = ((double)rate - mean) / sd;
            best = Math.Max(best, above ? 1 - NormalCdf(z) : NormalCdf(z));
        }

        return Math.Clamp(best, 0, 1);
    }

    private static (double Mean, double Sd) At(List<ForecastHorizon> points, double last, int day, bool above)
    {
        static double Side(ForecastHorizon h, bool above) =>
            Math.Max(1e-6, (double)(above ? h.Upper80 - h.Point : h.Point - h.Lower80) / Z80);

        double prevDay = 0, prevMean = last, prevVar = 0;
        foreach (var h in points)
        {
            var sd = Side(h, above);
            if (day <= h.Days)
            {
                var f = (day - prevDay) / (h.Days - prevDay);
                var mean = prevMean + ((double)h.Point - prevMean) * f;
                var variance = prevVar + (sd * sd - prevVar) * f;
                return (mean, Math.Sqrt(Math.Max(variance, 1e-12)));
            }

            prevDay = h.Days;
            prevMean = (double)h.Point;
            prevVar = sd * sd;
        }

        // Past the last stored horizon: hold its value and grow the spread at its own pace.
        var lastH = points[^1];
        var grown = prevVar * day / lastH.Days;
        return (prevMean, Math.Sqrt(Math.Max(grown, 1e-12)));
    }

    /// <summary>Standard normal CDF (Abramowitz &amp; Stegun 26.2.17, error below 7.5e-8).</summary>
    public static double NormalCdf(double z)
    {
        if (double.IsNaN(z))
        {
            return 0.5;
        }

        var t = 1 / (1 + 0.2316419 * Math.Abs(z));
        var d = 0.3989422804014327 * Math.Exp(-z * z / 2);
        var p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return z > 0 ? 1 - p : p;
    }
}
