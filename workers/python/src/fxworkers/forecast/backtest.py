"""Walk-forward backtest: forecast from past origins using only data up to each origin, score against actuals."""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass

from .models import History, Model, business_steps

ORIGIN_START = dt.date(2019, 1, 1)
ORIGIN_EVERY = 5  # business days between origins


@dataclass(frozen=True)
class Score:
    horizon_days: int
    origins_from: dt.date
    origins_to: dt.date
    n: int
    mae: float
    mape: float
    coverage80: float
    coverage95: float


def backtest(h: History, model: Model, horizon_days: int, start: dt.date = ORIGIN_START) -> Score | None:
    errors, pct, in80, in95, origins = [], [], 0, 0, []
    first = h.index_on_or_before(start) or 0
    for i in range(max(first, 1), len(h.dates), ORIGIN_EVERY):
        target = h.dates[i] + dt.timedelta(days=horizon_days)
        if target > h.dates[-1]:
            break
        steps = business_steps(h.dates[i], horizon_days)
        if i < model.min_history(steps):
            continue
        f = model.forecast(h, i, steps, horizon_days)
        if f is None:
            continue
        actual = h.rates[h.index_on_or_before(target)]
        errors.append(abs(f.point - actual))
        pct.append(abs(f.point - actual) / actual * 100)
        in80 += f.lower80 <= actual <= f.upper80
        in95 += f.lower95 <= actual <= f.upper95
        origins.append(h.dates[i])
    if not errors:
        return None
    n = len(errors)
    return Score(horizon_days, origins[0], origins[-1], n, sum(errors) / n, sum(pct) / n, in80 / n, in95 / n)
