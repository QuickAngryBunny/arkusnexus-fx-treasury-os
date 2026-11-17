import datetime as dt
import math
import random

from fxworkers.forecast.backtest import backtest
import pytest

from fxworkers.forecast.models import (HORIZONS, MODELS, Curves, CurveSeries, History, MarketForward, business_steps,
                                       forward_rate)


def flat_curves(start, days, mxn_pct=10.0, usd_pct=4.0):
    dates = [start + dt.timedelta(days=k) for k in range(days)]
    return Curves({
        "MXN": [CurveSeries(28, 360, dates, [mxn_pct] * days), CurveSeries(182, 360, dates, [mxn_pct] * days)],
        "USD": [CurveSeries(30, 365, dates, [usd_pct] * days), CurveSeries(182, 365, dates, [usd_pct] * days)],
    })


def random_walk(days=3000, vol=0.006, seed=7, curves=False):
    rng = random.Random(seed)
    dates, rates, day, log = [], [], dt.date(2010, 1, 1), math.log(15.0)
    while len(dates) < days:
        if day.weekday() < 5:
            log += rng.gauss(0, vol)
            dates.append(day)
            rates.append(math.exp(log))
        day += dt.timedelta(days=1)
    return History(dates, rates, flat_curves(dt.date(2010, 1, 1), 5000) if curves else None)


def test_business_steps_skip_weekends():
    assert business_steps(dt.date(2026, 11, 13), 3) == 1  # Fri → Sat, Sun, Mon
    assert business_steps(dt.date(2026, 11, 10), 7) == 5


def test_every_model_gives_an_ordered_positive_interval_for_every_horizon():
    h = random_walk(curves=True)
    i = len(h.dates) - 1
    for m in MODELS:
        for hz in HORIZONS:
            f = m.forecast(h, i, business_steps(h.dates[i], hz), hz)
            assert f is not None
            assert 0 < f.lower95 <= f.lower80 <= f.point <= f.upper80 <= f.upper95


def test_intervals_are_calibrated_on_a_true_random_walk():
    h = random_walk()
    for m in (m for m in MODELS if not isinstance(m, MarketForward)):
        s = backtest(h, m, 30, start=dt.date(2016, 1, 1))
        assert s is not None and s.n > 100
        assert 0.70 <= s.coverage80 <= 0.90, (m.code, s.coverage80)
        assert 0.88 <= s.coverage95 <= 0.99, (m.code, s.coverage95)


def test_backtest_is_deterministic_and_uses_only_past_data():
    h = random_walk()
    naive = MODELS[0]
    assert backtest(h, naive, 15, start=dt.date(2016, 1, 1)) == backtest(h, naive, 15, start=dt.date(2016, 1, 1))
    # Changing a rate after the last origin's target cannot change the score.
    s = backtest(h, naive, 15, start=dt.date(2016, 1, 1))
    later = History(h.dates + [h.dates[-1] + dt.timedelta(days=3)], h.rates + [h.rates[-1] * 10])
    assert backtest(later, naive, 15, start=dt.date(2016, 1, 1)).origins_to >= s.origins_to


def test_forward_follows_covered_interest_parity():
    # 10% MXN (ACT/360) vs 4% USD (ACT/365) over 90 days from 18.00.
    expected = 18.0 * (1 + 0.10 * 90 / 360) / (1 + 0.04 * 90 / 365)
    assert forward_rate(18.0, (0.10, 360), (0.04, 365), 90) == pytest.approx(expected)
    h = random_walk(curves=True)
    i = len(h.dates) - 1
    fwd = next(m for m in MODELS if m.code == "forward")
    f90 = fwd.forecast(h, i, business_steps(h.dates[i], 90), 90)
    f180 = fwd.forecast(h, i, business_steps(h.dates[i], 180), 180)
    assert f90.point == pytest.approx(forward_rate(h.rates[i], (0.10, 360), (0.04, 365), 90))
    assert h.rates[i] < f90.point < f180.point  # higher peso rates → forward above spot, rising with the horizon


def test_curve_interpolates_between_tenors_and_ignores_stale_points():
    d = dt.date(2026, 11, 10)
    curves = Curves({"MXN": [CurveSeries(28, 360, [d], [6.0]), CurveSeries(91, 360, [d], [7.0])]})
    assert curves.simple_rate("MXN", d, 28) == (0.06, 360)
    assert curves.simple_rate("MXN", d, 7) == (0.06, 360)  # flat below the shortest tenor
    rate, _ = curves.simple_rate("MXN", d, 60)
    assert rate == pytest.approx(0.06 + 0.01 * (60 - 28) / (91 - 28))
    assert curves.simple_rate("MXN", d + dt.timedelta(days=30), 28) is None  # older than 10 days
    assert curves.simple_rate("USD", d, 28) is None


def test_forward_model_publishes_nothing_without_curves_and_is_calibrated_with_them():
    fwd = next(m for m in MODELS if m.code == "forward")
    bare = random_walk()
    assert fwd.forecast(bare, len(bare.dates) - 1, 20, 30) is None
    assert backtest(bare, fwd, 30, start=dt.date(2016, 1, 1)) is None
    # On a pure random walk the forward's drift is a bias, yet its band should still hold most outcomes.
    s = backtest(random_walk(curves=True, vol=0.006), fwd, 30, start=dt.date(2016, 1, 1))
    assert s is not None and 0.65 <= s.coverage80 <= 0.92
