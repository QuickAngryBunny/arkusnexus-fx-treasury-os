namespace FxOs.Platform.Alerts;

/// <summary>An alert's settings and checker state (tenant <c>rate_alert</c>).</summary>
public sealed record AlertState(
    string Direction, decimal Rate, bool EarlyWarning, int LookAheadDays, bool KeepOn,
    bool IsOn, bool Armed, bool Warned, DateOnly? CheckedFixDate, long? CheckedRunId);

public sealed record Fix(DateOnly Date, decimal Rate);

/// <summary>An email-worthy outcome of one check.</summary>
public sealed record AlertEventDraft(string Kind, DateOnly FixDate, decimal FixRate, long? RunId, double? Chance, int? TrendMoves, int? TrendWindow);

public static class AlertEventKind
{
    public const string Reached = "reached";
    public const string EarlyWarning = "early_warning";
}

/// <summary>
/// One check of one alert, as a pure function of its state, the FIX history and the stored forecast. Replaying the same
/// FIX or forecast run returns the same state and no events.
/// </summary>
public static class RateAlertRules
{
    /// <param name="fixes">Recent FIXes, oldest first, ending with the latest (enough for the trend window and every FIX since the last check).</param>
    public static (AlertState Next, IReadOnlyList<AlertEventDraft> Events) Check(AlertState s, IReadOnlyList<Fix> fixes, StoredForecast? forecast)
    {
        var events = new List<AlertEventDraft>();
        if (fixes.Count == 0 || !s.IsOn)
        {
            return (s, events);
        }

        var latest = fixes[^1];
        var state = s;

        // Reached: every FIX since the last check, in order, so a keep-on alert sends one email per crossing.
        foreach (var fix in fixes.Where(f => s.CheckedFixDate is null ? f == latest : f.Date > s.CheckedFixDate))
        {
            var past = RateAlertMath.IsPast(state.Direction, state.Rate, fix.Rate);
            if (state.Armed && past)
            {
                events.Add(new AlertEventDraft(AlertEventKind.Reached, fix.Date, fix.Rate, forecast?.RunId, null, null, null));
                state = state.KeepOn
                    ? state with { Armed = false, Warned = false }
                    : state with { IsOn = false, Warned = false };
                if (!state.IsOn)
                {
                    break;
                }
            }
            else if (!state.Armed && !past)
            {
                state = state with { Armed = true };
            }
        }

        state = state with { CheckedFixDate = latest.Date, CheckedRunId = forecast?.RunId ?? state.CheckedRunId };

        // Early warning: only while the alert waits for the rate (on, armed, not already past it).
        if (state.IsOn && state.EarlyWarning && state.Armed && forecast is not null
            && !RateAlertMath.IsPast(state.Direction, state.Rate, latest.Rate))
        {
            var rates = fixes.Select(f => f.Rate).ToList();
            var (_, window) = RateAlertMath.TrendRule(state.LookAheadDays);
            var trend = RateAlertMath.TrendHolds(state.Direction, rates, state.LookAheadDays);
            var chance = RateAlertMath.ChanceWithin(state.Direction, state.Rate, forecast.LastRate, forecast.Horizons, state.LookAheadDays);
            var both = trend && chance > 0.5;
            if (both && !state.Warned)
            {
                events.Add(new AlertEventDraft(AlertEventKind.EarlyWarning, latest.Date, latest.Rate, forecast.RunId, chance,
                    RateAlertMath.MovesToward(state.Direction, rates, window), window));
                state = state with { Warned = true };
            }
            else if (!both && state.Warned)
            {
                state = state with { Warned = false };
            }
        }

        return (state, events);
    }
}
