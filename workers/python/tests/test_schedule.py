import datetime as dt

from fxworkers.rates.schedule import MEXICO_CITY, Schedule, next_run

S = Schedule()


def at(y, m, d, h, mi=0):
    return dt.datetime(y, m, d, h, mi, tzinfo=MEXICO_CITY)


def test_before_the_window_waits_for_its_start():
    assert next_run(at(2026, 11, 10, 9), S, done_today=False) == at(2026, 11, 10, 12, 30)


def test_inside_the_window_and_not_done_runs_now():
    now = at(2026, 11, 10, 13, 5)
    assert next_run(now, S, done_today=False) == now


def test_done_or_window_closed_moves_to_next_business_day():
    assert next_run(at(2026, 11, 10, 13), S, done_today=True) == at(2026, 11, 11, 12, 30)
    assert next_run(at(2026, 11, 10, 19), S, done_today=False) == at(2026, 11, 11, 12, 30)


def test_friday_after_the_window_skips_the_weekend():
    assert next_run(at(2026, 11, 13, 19), S, done_today=False) == at(2026, 11, 16, 12, 30)
    assert next_run(at(2026, 11, 14, 13), S, done_today=False) == at(2026, 11, 16, 12, 30)
