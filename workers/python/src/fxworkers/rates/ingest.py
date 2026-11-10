"""Fetch once, keep the raw response, then load it into every tenant that has the source enabled."""

from __future__ import annotations

import datetime as dt
import sys

import psycopg

from ..control_plane import Tenant, connect_tenant, list_tenants
from . import banxico
from .loader import SourceNotEnabledError, load_observations, record_failure, summary
from .raw_store import RawObject, RawStore, get_raw, put_raw
from .sources import Source


def fetch_to_store(store: RawStore, source: Source, start: dt.date, end: dt.date) -> RawObject:
    if source.provider != "banxico":
        raise NotImplementedError(f"No connector for provider '{source.provider}' yet.")
    data = banxico.fetch_range(source.series_id, start, end)
    banxico.parse(data, source.series_id)  # validate before anything is kept or loaded
    return put_raw(
        store,
        source.provider,
        source.series_id,
        data,
        {"provider": source.provider, "series": source.series_id, "range_from": start.isoformat(),
         "range_to": end.isoformat()},
    )


def load_from_store(cp: psycopg.Connection, store: RawStore, source: Source, key: str,
                    tenants: list[Tenant] | None = None) -> int:
    """Load one raw object into the given tenants (default: every active tenant). Returns the failure count."""
    raw = get_raw(store, key)
    observations = banxico.parse(raw.data, source.series_id)
    targets = tenants if tenants is not None else [t for t in list_tenants(cp) if t.status == "active"]
    failures = 0
    for tenant in targets:
        started = dt.datetime.now(dt.timezone.utc)
        try:
            with connect_tenant(cp, tenant) as conn:
                try:
                    result = load_observations(conn, source.code, raw.key, raw.sha256, observations, started)
                    print(f"{tenant.slug}: {summary(result)}")
                except SourceNotEnabledError as e:
                    print(f"{tenant.slug}: skipped ({e})")
                except Exception as e:  # recorded per tenant; other tenants still load
                    conn.rollback()
                    record_failure(conn, source.code, raw.key, raw.sha256, str(e), started)
                    raise
        except Exception as e:
            failures += 1
            print(f"{tenant.slug}: FAILED ({e})", file=sys.stderr)
    return failures
