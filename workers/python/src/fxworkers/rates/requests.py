"""Manual sync requests ("Sync now" on the Full history page).

The API only records a request in the tenant database; this module picks up pending requests from every tenant,
fetches each source once for the union of the requested ranges, loads every active tenant (idempotent), and marks
each request done or failed with a one-line result.
"""

from __future__ import annotations

import datetime as dt
import io
import sys
from contextlib import redirect_stdout

import psycopg

from ..control_plane import connect_tenant, list_tenants
from .ingest import fetch_to_store, load_from_store
from .raw_store import RawStore
from .sources import get_source

DEFAULT_DAYS = 30


def process_pending(cp: psycopg.Connection, store: RawStore, today: dt.date | None = None) -> int:
    """Handle every pending request. Returns how many requests were processed."""
    today = today or dt.date.today()
    tenants = [t for t in list_tenants(cp) if t.status == "active"]

    # 1. Claim pending requests (status → running), per tenant.
    claimed: dict[str, list[tuple[object, int, dt.date | None, dt.date | None]]] = {}
    for tenant in tenants:
        with connect_tenant(cp, tenant) as conn, conn.transaction():
            rows = conn.execute(
                """
                update sync_request set status = 'running', started_at = now()
                where status = 'pending' returning id, source_code, range_from, range_to
                """
            ).fetchall()
        for request_id, source_code, start, end in rows:
            claimed.setdefault(source_code, []).append((tenant, request_id, start, end))

    # 2. One fetch per source for the union of ranges, then load every tenant.
    for source_code, requests in claimed.items():
        source = get_source(source_code)
        start = min((r[2] or today - dt.timedelta(days=DEFAULT_DAYS)) for r in requests)
        end = min(max((r[3] or today) for r in requests), today)
        try:
            raw = fetch_to_store(store, source, max(start, source.first_date), end)
            log = io.StringIO()
            with redirect_stdout(log):
                failures = load_from_store(cp, store, source, raw.key, tenants)
            outcome = "failed" if failures else "done"
            result = f"{start.isoformat()}–{end.isoformat()}: {_mine_summary(log.getvalue())}"
        except Exception as e:  # recorded on every request; the scheduler keeps running
            outcome, result = "failed", f"{type(e).__name__}: {e}"[:500]
            print(f"{source_code}: sync request FAILED ({e})", file=sys.stderr, flush=True)

        # 3. Close each request in its own tenant, with that tenant's own load line.
        for tenant, request_id, _, _ in requests:
            line = result
            if outcome == "done":
                line = f"{start.isoformat()}–{end.isoformat()}: " + _line_for(log.getvalue(), tenant.slug)
            with connect_tenant(cp, tenant) as conn, conn.transaction():
                conn.execute(
                    "update sync_request set status = %s, finished_at = now(), result = %s where id = %s",
                    (outcome, line, request_id),
                )
        print(f"{source_code}: {len(requests)} request(s) {outcome}", flush=True)
    return sum(len(v) for v in claimed.values())


def _line_for(log: str, slug: str) -> str:
    for line in log.splitlines():
        if line.startswith(f"{slug}: "):
            return line[len(slug) + 2:]
    return "loaded"


def _mine_summary(log: str) -> str:
    return "; ".join(log.strip().splitlines())[:500]
