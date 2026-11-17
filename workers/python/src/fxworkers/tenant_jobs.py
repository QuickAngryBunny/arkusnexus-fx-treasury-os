"""The worker's part of the tenant admin portal (control-plane table tenant_job).

- setup / rate_history: a new tenant gets the full rate history by replaying every stored raw FIX response in fetch
  order (no outside call; the result matches every other tenant), then interest-rate curves and news (best effort),
  then its first forecast. Progress is written back so the portal's A-03 page can show it. When done the job moves to
  the invitation step, which the API runs.
- load_dummy / clear_dummy: the deterministic dummy dataset (dummy tenants only).

Jobs are claimed with SKIP LOCKED, so several workers never run the same job.
"""

from __future__ import annotations

import datetime as dt
import io
import json
import sys
import uuid
from contextlib import redirect_stdout

import psycopg

from .control_plane import _SELECT, Tenant, connect_tenant
from .rates.raw_store import RawStore, get_raw

SETUP_SOURCES = ("banxico_fix",)


def find_tenant_by_id(cp: psycopg.Connection, tenant_id: uuid.UUID) -> Tenant | None:
    with cp.cursor() as cur:
        cur.execute(_SELECT + " where t.id = %s", (tenant_id,))
        row = cur.fetchone()
        return Tenant(*row) if row else None


def run(dsn: str, store: RawStore) -> int:
    """Process due jobs on an autocommit connection, so each status and progress update is visible at once."""
    with psycopg.connect(dsn, autocommit=True) as cp:
        return process_pending_jobs(cp, store)


def process_pending_jobs(cp: psycopg.Connection, store: RawStore) -> int:
    """Run every due worker job once (cp must be autocommit). Returns how many jobs ran."""
    ran = 0
    while (job := _claim(cp)) is not None:
        job_id, tenant_id, kind = job
        try:
            tenant = find_tenant_by_id(cp, tenant_id)
            if tenant is None:
                raise RuntimeError("The tenant has no database routing (deleted or not set up).")
            if kind == "setup":
                _copy_rate_history(cp, store, tenant, job_id)
                _advance(cp, job_id, "rate_history", "invitation", "full rate history copied")
            else:
                note = _dummy(cp, tenant, load=(kind == "load_dummy"))
                _finish(cp, job_id, note)
            print(f"{tenant.slug}: tenant job {kind} done", flush=True)
        except Exception as e:  # recorded on the job; the portal shows it and offers Retry
            print(f"tenant job {job_id} ({kind}) FAILED: {e}", file=sys.stderr, flush=True)
            _fail(cp, job_id, f"{type(e).__name__}: {e}"[:1000])
        ran += 1
    return ran


def _claim(cp: psycopg.Connection) -> tuple[uuid.UUID, uuid.UUID, str] | None:
    with cp.transaction(), cp.cursor() as cur:
        cur.execute(
            """
            update tenant_job set status = 'running', updated_at = now()
            where id = (
                select id from tenant_job
                where status = 'pending'
                  and ((kind = 'setup' and step = 'rate_history') or (kind in ('load_dummy', 'clear_dummy') and step = 'work'))
                order by created_at limit 1 for update skip locked)
            returning id, tenant_id, kind
            """
        )
        return cur.fetchone()


def _progress(cp: psycopg.Connection, job_id: uuid.UUID, percent: int) -> None:
    with cp.transaction():
        cp.execute("update tenant_job set progress = %s, updated_at = now() where id = %s", (max(0, min(100, percent)), job_id))


def _copy_rate_history(cp: psycopg.Connection, store: RawStore, tenant: Tenant, job_id: uuid.UUID) -> None:
    from .rates import banxico
    from .rates.loader import load_observations
    from .rates.sources import get_source

    for code in SETUP_SOURCES:
        source = get_source(code)
        keys = store.list(f"{source.provider}/{source.series_id}/")
        if not keys:
            raise RuntimeError(f"The raw store has no {source.code} history yet. Run `fxworkers rates backfill` first.")
        with connect_tenant(cp, tenant) as conn:
            for i, key in enumerate(keys, start=1):
                raw = get_raw(store, key)
                observations = banxico.parse(raw.data, source.series_id)
                load_observations(conn, source.code, raw.key, raw.sha256, observations, dt.datetime.now(dt.timezone.utc))
                if i == len(keys) or i % 10 == 0:
                    _progress(cp, job_id, int(80 * i / len(keys)))

    # Curves feed the forward model; news gives context. Neither blocks setup.
    try:
        from .rates.curves import CURVES_START, sync as curves_sync

        with redirect_stdout(io.StringIO()):
            curves_sync(cp, store, CURVES_START, dt.date.today(), [tenant])
    except Exception as e:
        print(f"{tenant.slug}: curves skipped ({e})", file=sys.stderr, flush=True)
    _progress(cp, job_id, 88)
    try:
        from .news.sync import sync as news_sync

        with redirect_stdout(io.StringIO()):
            news_sync(cp, store, [tenant], max_windows=20)
    except Exception as e:
        print(f"{tenant.slug}: news skipped ({e})", file=sys.stderr, flush=True)
    _progress(cp, job_id, 94)

    from .forecast.publish import publish

    with connect_tenant(cp, tenant) as conn:
        publish(conn)
    _progress(cp, job_id, 100)


def _dummy(cp: psycopg.Connection, tenant: Tenant, load: bool) -> str:
    from .dummy_data import build_dataset
    from .seed import clear_tenant, seed_tenant

    with connect_tenant(cp, tenant) as conn:
        counts = (seed_tenant if load else clear_tenant)(conn, tenant, build_dataset(tenant.slug))
    return ", ".join(f"{k} {v}" for k, v in counts.items())


def _advance(cp: psycopg.Connection, job_id: uuid.UUID, done: str, next_step: str, note: str) -> None:
    event = json.dumps([{"step": done, "at": dt.datetime.now(dt.timezone.utc).isoformat(), "note": note}])
    with cp.transaction():
        cp.execute(
            """
            update tenant_job set step = %s, status = 'pending', progress = 0, updated_at = now(),
                   events = events || %s::jsonb
            where id = %s
            """,
            (next_step, event, job_id),
        )


def _finish(cp: psycopg.Connection, job_id: uuid.UUID, note: str) -> None:
    event = json.dumps([{"step": "work", "at": dt.datetime.now(dt.timezone.utc).isoformat(), "note": note}])
    with cp.transaction():
        cp.execute(
            """
            update tenant_job set step = 'done', status = 'done', progress = 100, finished_at = now(), updated_at = now(),
                   events = events || %s::jsonb
            where id = %s
            """,
            (event, job_id),
        )


def _fail(cp: psycopg.Connection, job_id: uuid.UUID, error: str) -> None:
    with cp.transaction():
        cp.execute("update tenant_job set status = 'failed', error = %s, updated_at = now() where id = %s", (error, job_id))
