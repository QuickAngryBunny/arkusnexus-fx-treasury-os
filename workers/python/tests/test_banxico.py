import datetime as dt
import json
from decimal import Decimal

import pytest

from fxworkers.rates.banxico import InvalidPayloadError, Observation, parse


def payload(rows, series="SF43718"):
    return json.dumps({"bmx": {"series": [{"idSerie": series, "titulo": "FIX", "datos": rows}]}}).encode()


def test_parses_dates_values_and_missing_days_sorted():
    raw = payload([
        {"fecha": "11/11/2026", "dato": "N/E"},
        {"fecha": "10/11/2026", "dato": "18.6678"},
        {"fecha": "12/11/1991", "dato": "3.0735"},
    ])
    assert parse(raw, "SF43718") == [
        Observation(dt.date(1991, 11, 12), Decimal("3.0735")),
        Observation(dt.date(2026, 11, 10), Decimal("18.6678")),
        Observation(dt.date(2026, 11, 11), None),
    ]


def test_range_with_nothing_published_is_empty():
    raw = json.dumps({"bmx": {"series": [{"idSerie": "SF43718", "titulo": "FIX"}]}}).encode()
    assert parse(raw, "SF43718") == []


@pytest.mark.parametrize("rows", [
    [{"fecha": "2026-11-10", "dato": "18.6"}],                                    # wrong date format
    [{"fecha": "10/11/2026", "dato": "abc"}],                                     # not a number
    [{"fecha": "10/11/2026", "dato": "0"}],                                       # out of bounds
    [{"fecha": "10/11/2026", "dato": "18.6"}, {"fecha": "10/11/2026", "dato": "18.7"}],  # duplicate day
])
def test_rejects_invalid_rows(rows):
    with pytest.raises(InvalidPayloadError):
        parse(payload(rows), "SF43718")


def test_rejects_a_different_series_or_shape():
    with pytest.raises(InvalidPayloadError):
        parse(payload([], series="SF60653"), "SF43718")
    with pytest.raises(InvalidPayloadError):
        parse(b'{"error": "token"}', "SF43718")
