import datetime as dt
from decimal import Decimal

from fxworkers.dummy_treasury import BANK_SPREAD, build_treasury


def flat_fix(day):
    return (day, Decimal("18.500000"))


def no_fix(day):
    return None


def test_growing_the_range_never_changes_past_movements():
    short = build_treasury("demo_norte", dt.date(2025, 6, 30), flat_fix)
    long = build_treasury("demo_norte", dt.date(2026, 3, 31), flat_fix)
    assert set(short.transactions) <= set(long.transactions)
    assert build_treasury("demo_norte", dt.date(2025, 6, 30), flat_fix) == short


def test_every_movement_is_in_its_account_currency_and_inside_the_range():
    data = build_treasury("demo_bajio", dt.date(2025, 12, 31), flat_fix)
    currency = {a.id: a.currency for a in data.accounts}
    assert all(t.currency == currency[t.bank_account_id] for t in data.transactions)
    assert all(dt.date(2025, 1, 1) <= t.booked_on <= dt.date(2025, 12, 31) for t in data.transactions)
    assert all(t.booked_on.weekday() < 5 for t in data.transactions)
    assert len({t.id for t in data.transactions}) == len(data.transactions)


def test_conversions_have_two_matching_legs_with_the_applied_rate():
    data = build_treasury("demo_norte", dt.date(2025, 12, 31), flat_fix)
    legs = [t for t in data.transactions if t.category == "fx_conversion"]
    by_id = {}
    for t in legs:
        by_id.setdefault(t.conversion_id, []).append(t)
    assert len(by_id) == 12
    for usd, mxn in (sorted(pair, key=lambda t: t.currency, reverse=True) for pair in by_id.values()):
        assert (usd.currency, usd.direction, mxn.currency, mxn.direction) == ("USD", "expense", "MXN", "income")
        assert usd.fx_rate == mxn.fx_rate == Decimal("18.500000") - BANK_SPREAD
        assert mxn.amount == (usd.amount * usd.fx_rate).quantize(Decimal("0.01"))
        assert (usd.counter_amount, mxn.counter_amount) == (mxn.amount, usd.amount)
        assert usd.fx_rate_source and usd.fx_rate_at is not None


def test_no_stored_fix_means_no_conversion():
    data = build_treasury("demo_norte", dt.date(2025, 12, 31), no_fix)
    assert not [t for t in data.transactions if t.category == "fx_conversion"]


def test_december_has_the_aguinaldo_and_tenants_differ_in_size():
    norte = build_treasury("demo_norte", dt.date(2025, 12, 31), flat_fix)
    bajio = build_treasury("demo_bajio", dt.date(2025, 12, 31), flat_fix)
    assert [t.booked_on.month for t in norte.transactions if t.category == "year_end_bonus"] == [12]
    income = lambda d: sum(t.amount for t in d.transactions if t.category == "client_payment")  # noqa: E731
    assert income(bajio) < income(norte)
