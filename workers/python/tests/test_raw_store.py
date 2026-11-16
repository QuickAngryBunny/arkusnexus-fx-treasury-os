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


def test_list_returns_a_series_keys_in_fetch_order(tmp_path):
    import datetime as dt

    from fxworkers.rates.raw_store import LocalRawStore, put_raw

    store = LocalRawStore(tmp_path)
    t0 = dt.datetime(2026, 11, 10, 18, 0, tzinfo=dt.timezone.utc)
    later = put_raw(store, "banxico", "SF43718", b'{"b": 2}', {}, fetched_at=t0 + dt.timedelta(days=2))
    first = put_raw(store, "banxico", "SF43718", b'{"a": 1}', {}, fetched_at=t0)
    put_raw(store, "banxico", "SF60648", b'{"c": 3}', {}, fetched_at=t0)

    assert store.list("banxico/SF43718/") == [first.key, later.key]
    assert store.list("nothing/here/") == []


class _FakeBlob:
    def __init__(self, bucket, name):
        self.bucket, self.name, self.metadata = bucket, name, None

    def upload_from_string(self, data, content_type=None, if_generation_match=None):
        assert if_generation_match == 0
        if self.name in self.bucket.objects:
            raise FileExistsError(self.name)  # Cloud Storage answers 412 Precondition Failed
        self.bucket.objects[self.name] = data

    def download_as_bytes(self):
        return self.bucket.objects[self.name]


class _FakeBucket:
    def __init__(self):
        self.objects = {}

    def blob(self, name):
        return _FakeBlob(self, name)

    def list_blobs(self, prefix=""):
        return [_FakeBlob(self, n) for n in self.objects if n.startswith(prefix)]


class _FakeClient:
    def __init__(self):
        self.buckets = {}

    def bucket(self, name):
        return self.buckets.setdefault(name, _FakeBucket())


def test_cloud_storage_keeps_keys_under_its_prefix_and_never_overwrites():
    from fxworkers.rates.raw_store import GcsRawStore

    client = _FakeClient()
    store = GcsRawStore("fx-raw", "rates/", client=client)
    store.put("banxico/SF43718/a.json", b"{}", {"sha256": "x"})
    store.put("banxico/SF43718/b.json", b"[]", {})
    assert set(client.buckets["fx-raw"].objects) == {"rates/banxico/SF43718/a.json", "rates/banxico/SF43718/b.json"}
    assert store.list("banxico/SF43718/") == ["banxico/SF43718/a.json", "banxico/SF43718/b.json"]
    assert store.get("banxico/SF43718/a.json") == b"{}"
    import pytest

    with pytest.raises(FileExistsError):
        store.put("banxico/SF43718/a.json", b"changed", {})


def test_a_gs_url_selects_cloud_storage(monkeypatch):
    import fxworkers.rates.raw_store as rs

    seen = {}
    monkeypatch.setattr(rs, "GcsRawStore", lambda bucket, prefix: seen.update(bucket=bucket, prefix=prefix) or "gcs")
    monkeypatch.delenv("FX_RAW_STORE_DIR", raising=False)
    monkeypatch.setenv("FX_RAW_STORE_URL", "gs://fx-raw/rates")
    assert rs.open_store() == "gcs" and seen == {"bucket": "fx-raw", "prefix": "rates"}
    monkeypatch.setenv("FX_RAW_STORE_URL", "s3://nope")
    import pytest

    with pytest.raises(ValueError):
        rs.open_store()
