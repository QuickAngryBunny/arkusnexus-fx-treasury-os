"""Forecast models over a daily rate history (business days only).

Everything works on log rates, so intervals are asymmetric in rate terms and never go below zero.
Horizons are calendar days; a horizon of h days spans the business days between the as-of date and as-of + h.
"""

from __future__ import annotations

import bisect
import datetime as dt
import math
from dataclasses import dataclass, field

HORIZONS = (7, 15, 30, 60, 90, 180)
Z80 = 1.2815515655446004
Z95 = 1.959963984540054
LONG_WINDOW = 1260  # ~5 years of business days
TREND_WINDOW = 252  # ~1 year


@dataclass(frozen=True)
class Interval:
    point: float
    lower80: float
    upper80: float
    lower95: float
    upper95: float

    def ordered(self) -> "Interval":
        """Guarantee lower95 <= lower80 <= point <= upper80 <= upper95 (quantiles can cross on tiny samples)."""
        l80, u80 = min(self.lower80, self.point), max(self.upper80, self.point)
        return Interval(self.point, l80, u80, min(self.lower95, l80), max(self.upper95, u80))


MAX_CURVE_AGE = dt.timedelta(days=10)  # a curve point older than this (as of the origin) is not used


@dataclass
class CurveSeries:
    tenor_days: int
    day_count: int
    dates: list[dt.date]
    rates_pct: list[float]

    def on_or_before(self, day: dt.date) -> float | None:
        j = bisect.bisect_right(self.dates, day) - 1
        if j < 0 or day - self.dates[j] > MAX_CURVE_AGE:
            return None
        return self.rates_pct[j]


@dataclass
class Curves:
    """Short-term rates per currency, one series per tenor."""

    by_currency: dict[str, list[CurveSeries]]

    def simple_rate(self, currency: str, day: dt.date, tenor_days: int) -> tuple[float, int] | None:
        """Annual simple rate (fraction) for `tenor_days`, linearly interpolated across tenors known on `day`
        (flat beyond the shortest and longest), with its day-count basis."""
        points = sorted((s.tenor_days, v, s.day_count) for s in self.by_currency.get(currency, [])
                        if (v := s.on_or_before(day)) is not None)
        if not points:
            return None
        if tenor_days <= points[0][0]:
            return points[0][1] / 100, points[0][2]
        if tenor_days >= points[-1][0]:
            return points[-1][1] / 100, points[-1][2]
        for (t0, r0, b0), (t1, r1, _) in zip(points, points[1:]):
            if t0 <= tenor_days <= t1:
                return (r0 + (r1 - r0) * (tenor_days - t0) / (t1 - t0)) / 100, b0
        return None  # pragma: no cover


def forward_rate(spot: float, quote_rate: tuple[float, int], base_rate: tuple[float, int], days: int) -> float:
    """Covered interest parity: F = S × (1 + i_quote·d/B_quote) / (1 + i_base·d/B_base)."""
    (iq, bq), (ib, bb) = quote_rate, base_rate
    return spot * (1 + iq * days / bq) / (1 + ib * days / bb)


@dataclass
class History:
    """A rate series with the running sums the models need, computed once."""

    dates: list[dt.date]
    rates: list[float]
    curves: Curves | None = None
    logs: list[float] = field(init=False)
    rets: list[float] = field(init=False)      # rets[i] = log change from i-1 to i (rets[0] = 0)
    cum: list[float] = field(init=False)       # prefix sums of rets
    cum2: list[float] = field(init=False)      # prefix sums of rets²
    ewma_var: list[float] = field(init=False)  # causal EWMA variance of daily log changes (λ = 0.94)

    def __post_init__(self) -> None:
        if len(self.dates) != len(self.rates) or len(self.dates) < 2:
            raise ValueError("History needs at least two dated rates.")
        self.logs = [math.log(r) for r in self.rates]
        self.rets = [0.0] + [self.logs[i] - self.logs[i - 1] for i in range(1, len(self.logs))]
        self.cum, self.cum2 = [0.0], [0.0]
        for r in self.rets:
            self.cum.append(self.cum[-1] + r)
            self.cum2.append(self.cum2[-1] + r * r)
        lam, var = 0.94, (self.rets[1] ** 2 if len(self.rets) > 1 else 0.0)
        self.ewma_var = []
        for i, r in enumerate(self.rets):
            if i > 0:
                var = lam * var + (1 - lam) * r * r
            self.ewma_var.append(var)

    def mean_ret(self, i: int, window: int) -> float:
        lo = max(1, i - window + 1)
        return (self.cum[i + 1] - self.cum[lo]) / (i + 1 - lo)

    def mean_sq(self, i: int, window: int) -> float:
        lo = max(1, i - window + 1)
        return (self.cum2[i + 1] - self.cum2[lo]) / (i + 1 - lo)

    def index_on_or_before(self, day: dt.date) -> int | None:
        j = bisect.bisect_right(self.dates, day) - 1
        return j if j >= 0 else None


def business_steps(as_of: dt.date, horizon_days: int) -> int:
    """Business days in (as_of, as_of + horizon_days]."""
    steps, day = 0, as_of
    for _ in range(horizon_days):
        day += dt.timedelta(days=1)
        if day.weekday() < 5:
            steps += 1
    return max(steps, 1)


def _quantile(sorted_values: list[float], q: float) -> float:
    pos = q * (len(sorted_values) - 1)
    lo, hi = math.floor(pos), math.ceil(pos)
    return sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * (pos - lo)


@dataclass(frozen=True)
class Model:
    code: str
    version: str
    name: str
    description: str
    params: dict
    is_benchmark: bool = False
    display_order: int = 100

    def min_history(self, steps: int) -> int:
        return LONG_WINDOW + steps

    def forecast(self, h: History, i: int, steps: int, horizon_days: int) -> Interval | None:
        """None when the model lacks an input at origin i (e.g. no interest-rate curve)."""
        raise NotImplementedError


class Naive(Model):
    """Random walk: the rate stays where it is. Intervals are the empirical spread of past moves of the same length."""

    def forecast(self, h: History, i: int, steps: int, horizon_days: int) -> Interval | None:
        r = h.rates[i]
        lo = max(steps, i - self.params["window"] + 1)
        moves = sorted(h.logs[j] - h.logs[j - steps] for j in range(lo, i + 1))
        q = lambda p: r * math.exp(_quantile(moves, p))  # noqa: E731
        return Interval(r, q(0.10), q(0.90), q(0.025), q(0.975)).ordered()


class EwmaDrift(Model):
    """Optional shrunk one-year drift; volatility starts at the EWMA level and relaxes toward the 5-year level.

    Backtests from 2019 showed the drift (shrink 0.5) made point errors worse at every horizon (up to +16% MAE at
    180 days), so the registered model uses no drift: its value is an interval that reacts to the current regime.
    """

    def forecast(self, h: History, i: int, steps: int, horizon_days: int) -> Interval | None:
        center = self.center(h, i, steps, horizon_days)
        if center is None:
            return None
        sd = self.spread(h, i, steps)
        e = math.exp
        return Interval(e(center), e(center - Z80 * sd), e(center + Z80 * sd),
                        e(center - Z95 * sd), e(center + Z95 * sd)).ordered()

    def center(self, h: History, i: int, steps: int, horizon_days: int) -> float | None:
        return h.logs[i] + h.mean_ret(i, self.params["trend_window"]) * self.params["drift_shrink"] * steps

    def spread(self, h: History, i: int, steps: int) -> float:
        p = self.params
        ew, lr, phi = h.ewma_var[i], h.mean_sq(i, p["long_window"]), p["phi"]
        var = steps * lr + (ew - lr) * phi * (1 - phi**steps) / (1 - phi)
        return math.sqrt(max(var, 1e-12))


class MarketForward(EwmaDrift):
    """Point = the market forward from the interest-rate difference; interval = the EWMA volatility band around it.

    The forward is what a bank would quote today for that date, not a prediction of where the rate will go.
    """

    def center(self, h: History, i: int, steps: int, horizon_days: int) -> float | None:
        if h.curves is None:
            return None
        day = h.dates[i]
        quote = h.curves.simple_rate(self.params["quote_currency"], day, horizon_days)
        base = h.curves.simple_rate(self.params["base_currency"], day, horizon_days)
        if quote is None or base is None:
            return None
        return math.log(forward_rate(h.rates[i], quote, base, horizon_days))


MODELS: tuple[Model, ...] = (
    Naive("naive", "1", "Naive (random walk)",
          "Benchmark. Point = last rate; intervals from past moves of the same length over 5 years.",
          {"window": LONG_WINDOW}, is_benchmark=True, display_order=99),
    EwmaDrift("ewma_vol", "1", "EWMA volatility",
              "Point = last rate; interval width starts at EWMA(0.94) volatility and relaxes toward 5-year "
              "volatility (φ = 0.97), so ranges widen quickly in turbulent markets.",
              {"trend_window": TREND_WINDOW, "drift_shrink": 0.0, "long_window": LONG_WINDOW, "phi": 0.97},
              display_order=2),
    MarketForward("forward", "1", "Market forward",
                  "Point = spot × (1 + TIIE·d/360) / (1 + US Treasury·d/365), the forward implied by the MXN–USD "
                  "interest-rate difference (tenor interpolated); interval = EWMA volatility band around it.",
                  {"trend_window": TREND_WINDOW, "drift_shrink": 0.0, "long_window": LONG_WINDOW, "phi": 0.97,
                   "base_currency": "USD", "quote_currency": "MXN"},
                  display_order=1),
)
