"""Flagged moves: days whose FIX change is beyond 2 standard deviations of the previous 30 days of daily changes."""

from __future__ import annotations

import datetime as dt
import math
from dataclasses import dataclass

WINDOW = 21          # business days ≈ 30 calendar days
THRESHOLD = 2.0      # standard deviations
TRADING_DAYS = 252


@dataclass(frozen=True)
class Move:
    date: dt.date
    rate: float
    change_pct: float  # vs the previous FIX
    sigma: float       # change / std of the previous WINDOW daily log changes
    vol30_pct: float   # annualized volatility of those previous changes


def score(dates: list[dt.date], rates: list[float], i: int) -> Move | None:
    """The move on dates[i], measured against the WINDOW daily changes before it (None without enough history)."""
    if i < WINDOW + 1 or i >= len(rates):
        return None
    rets = [math.log(rates[k] / rates[k - 1]) for k in range(i - WINDOW, i)]
    mean = sum(rets) / WINDOW
    sd = math.sqrt(sum((r - mean) ** 2 for r in rets) / (WINDOW - 1))
    if sd == 0:
        return None
    today = math.log(rates[i] / rates[i - 1])
    return Move(dates[i], rates[i], (rates[i] / rates[i - 1] - 1) * 100, today / sd, sd * math.sqrt(TRADING_DAYS) * 100)


def flagged(dates: list[dt.date], rates: list[float], since: dt.date | None = None) -> list[Move]:
    out = []
    for i in range(len(dates)):
        if since and dates[i] < since:
            continue
        m = score(dates, rates, i)
        if m and abs(m.sigma) >= THRESHOLD:
            out.append(m)
    return out
