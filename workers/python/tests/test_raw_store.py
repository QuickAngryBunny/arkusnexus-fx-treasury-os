import datetime as dt

import pytest

from fxworkers.rates.raw_store import LocalRawStore, get_raw, make_key, put_raw, sha256


def test_key_carries_provider_series_time_and_hash():
    at = dt.datetime(2026, 11, 10, 21, 3, 49, tzinfo=dt.timezone.utc)
    assert make_key("banxico", "SF43718", at, "e869d47256e4ffff") == (
        "banxico/SF43718/2026/11/10/20261110T210349Z-e869d47256e4.json"
    )


def test_objects_round_trip_and_are_never_overwritten(tmp_path):
    store = LocalRawStore(tmp_path)
    at = dt.datetime(2026, 11, 10, tzinfo=dt.timezone.utc)
    obj = put_raw(store, "banxico", "SF43718", b'{"a":1}', {}, fetched_at=at)

    again = get_raw(store, obj.key)
    assert again.data == b'{"a":1}' and again.sha256 == sha256(b'{"a":1}')
    with pytest.raises(FileExistsError):
        put_raw(store, "banxico", "SF43718", b'{"a":1}', {}, fetched_at=at)
