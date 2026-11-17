"""Daily rate sync schedule.

Banxico determines the FIX at 12:00 Mexico City time on business days and publishes it in SIE shortly after.
The scheduler syncs once per business day after `at`, then retries every `retry` until today's FIX is stored or
the window closes at `until`. One fetch per attempt is fanned out to every tenant (see ingest.py).
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass
from zoneinfo import ZoneInfo

MEXICO_CITY = ZoneInfo("America/Mexico_City")


@dataclass(frozen=True)
class Schedule:
    at: dt.time = dt.time(12, 30)
    until: dt.time = dt.time(18, 0)
    retry: dt.timedelta = dt.timedelta(minutes=30)
    tz: ZoneInfo = MEXICO_CITY


def is_business_day(day: dt.date) -> bool:
    # Weekends only; bank holidays simply produce no new FIX, and the retry window ends the day.
    return day.weekday() < 5


def next_run(now: dt.datetime, schedule: Schedule, done_today: bool) -> dt.datetime:
    """When to run next, given the current time and whether today's FIX is already stored."""
    local = now.astimezone(schedule.tz)
    today = local.date()
    start = dt.datetime.combine(today, schedule.at, schedule.tz)
    end = dt.datetime.combine(today, schedule.until, schedule.tz)

    if is_business_day(today) and not done_today:
        if local < start:
            return start
        if local < end:
            return local  # inside the window and not done: run now (callers wait `retry` between attempts)

    day = today + dt.timedelta(days=1)
    while not is_business_day(day):
        day += dt.timedelta(days=1)
    return dt.datetime.combine(day, schedule.at, schedule.tz)
