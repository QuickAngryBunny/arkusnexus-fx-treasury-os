using FxOs.Platform.Alerts;

namespace FxOs.Api.Tests;

public sealed class RateAlertMathTests
{
    // 7D 18.48 (18.21–18.76), 15D 18.54 (18.11–18.98), 30D 18.61 (18.02–19.22) from today's 18.4215 (prototype P2-04).
    private static readonly ForecastHorizon[] Run =
    [
        new(7, 18.48m, 18.21m, 18.76m),
        new(15, 18.54m, 18.11m, 18.98m),
        new(30, 18.61m, 18.02m, 19.22m),
    ];

    private const decimal Today = 18.4215m;

    [Theory]
    [InlineData(7, 3, 5)]
    [InlineData(15, 5, 8)]
    [InlineData(30, 7, 12)]
    public void Longer_look_aheads_need_a_longer_trend(int lookAhead, int needed, int window) =>
        Assert.Equal((needed, window), RateAlertMath.TrendRule(lookAhead));

    [Fact]
    public void Trend_counts_moves_toward_the_rate_over_the_window()
    {
        decimal[] fixes = [18.0m, 18.1m, 18.2m, 18.15m, 18.3m, 18.4m];
        Assert.Equal(4, RateAlertMath.MovesToward(AlertDirection.Above, fixes, 5));
        Assert.Equal(1, RateAlertMath.MovesToward(AlertDirection.Below, fixes, 5));
        Assert.True(RateAlertMath.TrendHolds(AlertDirection.Above, fixes, 7));
        Assert.False(RateAlertMath.TrendHolds(AlertDirection.Below, fixes, 7));
        // Not enough days for the 15-day rule (8 moves).
        Assert.False(RateAlertMath.TrendHolds(AlertDirection.Above, fixes, 15));
        // A flat day is not a move toward the rate.
        Assert.Equal(0, RateAlertMath.MovesToward(AlertDirection.Above, [18m, 18m, 18m], 5));
    }

    [Fact]
    public void Reached_means_at_or_past_the_rate()
    {
        Assert.True(RateAlertMath.IsPast(AlertDirection.Above, 19m, 19m));
        Assert.True(RateAlertMath.IsPast(AlertDirection.Above, 19m, 19.01m));
        Assert.False(RateAlertMath.IsPast(AlertDirection.Above, 19m, 18.99m));
        Assert.True(RateAlertMath.IsPast(AlertDirection.Below, 18m, 18m));
        Assert.False(RateAlertMath.IsPast(AlertDirection.Below, 18m, 18.01m));
    }

    [Fact]
    public void Chance_reads_the_stored_range()
    {
        // The top of the 30-day 8-in-10 range has a 1-in-10 chance by then.
        Assert.InRange(RateAlertMath.ChanceWithin(AlertDirection.Above, 19.22m, Today, Run, 30), 0.095, 0.105);
        Assert.InRange(RateAlertMath.ChanceWithin(AlertDirection.Below, 18.02m, Today, Run, 30), 0.095, 0.105);
        // The central value is a coin flip.
        Assert.InRange(RateAlertMath.ChanceWithin(AlertDirection.Above, 18.61m, Today, Run, 30), 0.49, 0.51);
        // Already past today: certain. Far away: next to nothing.
        Assert.Equal(1, RateAlertMath.ChanceWithin(AlertDirection.Above, 18.40m, Today, Run, 7));
        Assert.True(RateAlertMath.ChanceWithin(AlertDirection.Above, 21m, Today, Run, 30) < 0.001);
    }

    [Fact]
    public void A_longer_look_ahead_never_lowers_the_chance()
    {
        var c7 = RateAlertMath.ChanceWithin(AlertDirection.Above, 19m, Today, Run, 7);
        var c15 = RateAlertMath.ChanceWithin(AlertDirection.Above, 19m, Today, Run, 15);
        var c30 = RateAlertMath.ChanceWithin(AlertDirection.Above, 19m, Today, Run, 30);
        Assert.True(c7 <= c15 && c15 <= c30);
        Assert.True(c7 < 0.05);
    }

    [Fact]
    public void Normal_cdf_matches_known_values()
    {
        Assert.Equal(0.5, RateAlertMath.NormalCdf(0), 7);
        Assert.Equal(0.9, RateAlertMath.NormalCdf(RateAlertMath.Z80), 6);
        Assert.Equal(0.0227501, RateAlertMath.NormalCdf(-2), 6);
    }
}
