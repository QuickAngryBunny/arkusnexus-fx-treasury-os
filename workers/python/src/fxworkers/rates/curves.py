"""Short-term interest-rate curves: Banxico TIIE (MXN) and the US Treasury daily yield curve (USD).

Same flow as exchange rates: fetch each source once, keep the raw response in the raw store, validate, then load
every active tenant (idempotent; a revised value is replaced, versioned and audited).
"""

from __future__ import annotations

import csv
import datetime as dt
import io
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation

import psycopg

from ..control_plane import Tenant, connect_tenant, list_tenants
from . import banxico
from .banxico import InvalidPayloadError, Observation, SourceUnavailableError
from .raw_store import RawStore, put_raw

ACTOR = "rate-ingestion"
TREASURY_URL = ("https://home.treasury.gov/resource-center/data-chart-center/interest-rates/daily-treasury-rates.csv/"
                "{year}/all?type=daily_treasury_yield_curve&field_tdr_date_value={year}&page&_format=csv")
MIN_PCT, MAX_PCT = Decimal("-10"), Decimal("100")
CURVES_START = dt.date(2018, 1, 1)  # enough history for forecast backtests from 2019


@dataclass(frozen=True)
class Series:
    code: str
    provider: str
    series_id: str  # Banxico series id, or the Treasury CSV column


SERIES: tuple[Series, ...] = (
    Series("tiie_28", "banxico", "SF43783"),
    Series("tiie_91", "banxico", "SF43878"),
    Series("tiie_182", "banxico", "SF111916"),
    Series("ust_1m", "us_treasury", "1 Mo"),
    Series("ust_2m", "us_treasury", "2 Mo"),
    Series("ust_3m", "us_treasury", "3 Mo"),
    Series("ust_4m", "us_treasury", "4 Mo"),
    Series("ust_6m", "us_treasury", "6 Mo"),
    Series("ust_1y", "us_treasury", "1 Yr"),
)


def fetch_treasury_year(year: int, attempts: int = 3) -> bytes:
    request = urllib.request.Request(TREASURY_URL.format(year=year), headers={"User-Agent": banxico.USER_AGENT})
    last: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError) as e:
            last = e
            if attempt < attempts:
                time.sleep(2 ** attempt)
    raise SourceUnavailableError(f"US Treasury yield curve {year} unreachable: {last}")


def parse_treasury(raw: bytes) -> dict[str, list[Observation]]:
    """Columns → observations (percent). Blank or N/A cells are days without a value."""
    rows = list(csv.DictReader(io.StringIO(raw.decode("utf-8-sig"))))
    if rows and "Date" not in rows[0]:
        raise InvalidPayloadError("Treasury CSV has no Date column.")
    wanted = [s.series_id for s in SERIES if s.provider == "us_treasury"]
    out: dict[str, list[Observation]] = {c: [] for c in wanted}
    seen: set[dt.date] = set()
    for row in rows:
        try:
            day = dt.datetime.strptime(row["Date"], "%m/%d/%Y").date()
        except (KeyError, ValueError) as e:
            raise InvalidPayloadError(f"Bad date in Treasury row {row!r}.") from e
        if day in seen:
            raise InvalidPayloadError(f"Duplicate date {day} in Treasury CSV.")
        seen.add(day)
        for column in wanted:
            text = (row.get(column) or "").strip()
            if text in ("", "N/A"):
                out[column].append(Observation(day, None))
                continue
            try:
                value = Decimal(text)
            except InvalidOperation as e:
                raise InvalidPayloadError(f"Bad value {text!r} for {column} on {day}.") from e
            if not (MIN_PCT < value < MAX_PCT):
                raise InvalidPayloadError(f"Value {value} for {column} on {day} is out of range.")
            out[column].append(Observation(day, value))
    for column in out:
        out[column].sort(key=lambda o: o.date)
    return out


def load_interest(conn: psycopg.Connection, series_code: str, raw_key: str, observations: list[Observation]) -> tuple[int, int]:
    """Insert new values, replace revised ones (revision + 1, audited). Returns (inserted, revised)."""
    values = [o for o in observations if o.value is not None]
    if not values:
        return 0, 0
    with conn.transaction(), conn.cursor() as cur:
        cur.execute("select is_enabled from interest_series where code = %s", (series_code,))
        row = cur.fetchone()
        if row is None or not row[0]:
            return 0, 0
        cur.execute(
            "select rate_date, rate_pct from interest_rate where series_code = %s and rate_date between %s and %s",
            (series_code, values[0].date, values[-1].date),
        )
        existing = dict(cur.fetchall())
        new = [(series_code, o.date, o.value, raw_key) for o in values if o.date not in existing]
        revised = [{"date": o.date.isoformat(), "old": str(existing[o.date]), "new": str(o.value)}
                   for o in values if o.date in existing and existing[o.date] != o.value]
        if new:
            cur.executemany("insert into interest_rate (series_code, rate_date, rate_pct, raw_ref) values (%s, %s, %s, %s)", new)
        if revised:
            cur.executemany(
                """update interest_rate set rate_pct = %s, revision = revision + 1, raw_ref = %s, loaded_at = now()
                   where series_code = %s and rate_date = %s""",
                [(Decimal(r["new"]), raw_key, series_code, dt.date.fromisoformat(r["date"])) for r in revised],
            )
        if new or revised:
            cur.execute(
                """insert into audit_log (actor, action, entity_type, entity_id, data)
                   values (%s, 'interest_rates.loaded', 'interest_series', %s, %s::jsonb)""",
                (ACTOR, series_code, json.dumps({"raw_ref": raw_key, "inserted": len(new), "revised": len(revised),
                                                 "revisions": revised[:200]})),
            )
    return len(new), len(revised)


def sync(cp: psycopg.Connection, store: RawStore, start: dt.date, end: dt.date,
         tenants: list[Tenant] | None = None) -> int:
    """Fetch every curve series for [start, end] once and load it into the tenants. Returns the failure count."""
    batches: list[tuple[str, str, list[Observation]]] = []  # (series_code, raw_key, observations)
    failures = 0

    for s in (s for s in SERIES if s.provider == "banxico"):
        try:
            data = banxico.fetch_range(s.series_id, start, end)
            obs = banxico.parse(data, s.series_id)
            raw = put_raw(store, "banxico", s.series_id, data, {"provider": "banxico", "series": s.series_id,
                                                                "range_from": start.isoformat(), "range_to": end.isoformat()})
            batches.append((s.code, raw.key, obs))
        except Exception as e:
            failures += 1
            print(f"{s.code}: FAILED to fetch ({e})", file=sys.stderr, flush=True)

    for year in range(start.year, end.year + 1):
        try:
            data = fetch_treasury_year(year)
            parsed = parse_treasury(data)
            raw = put_raw(store, "us_treasury", f"yield_curve_{year}", data, {"provider": "us_treasury", "year": str(year)})
            for s in (s for s in SERIES if s.provider == "us_treasury"):
                batches.append((s.code, raw.key, [o for o in parsed[s.series_id] if start <= o.date <= end]))
        except Exception as e:
            failures += 1
            print(f"us_treasury {year}: FAILED to fetch ({e})", file=sys.stderr, flush=True)

    targets = tenants if tenants is not None else [t for t in list_tenants(cp) if t.status == "active"]
    for tenant in targets:
        try:
            with connect_tenant(cp, tenant) as conn:
                totals = [load_interest(conn, code, key, obs) for code, key, obs in batches]
            print(f"{tenant.slug}: curves inserted {sum(t[0] for t in totals)}, revised {sum(t[1] for t in totals)}",
                  flush=True)
        except Exception as e:
            failures += 1
            print(f"{tenant.slug}: curves FAILED ({e})", file=sys.stderr, flush=True)
    return failures
