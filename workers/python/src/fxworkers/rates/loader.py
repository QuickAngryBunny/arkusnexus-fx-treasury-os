"""Load validated observations into one tenant database.

Idempotent: a value already stored is left alone, so rerunning a load changes no rates. A source that revises a
past value replaces it, bumps `revision` and writes an audit entry in the same transaction. Every attempt, even one
that changes nothing, is recorded in rate_load.
"""

from __future__ import annotations

import datetime as dt
import json
from dataclasses import asdict, dataclass
from decimal import Decimal

import psycopg

from .banxico import Observation

ACTOR = "rate-ingestion"
MAX_AUDITED_REVISIONS = 200


class SourceNotEnabledError(RuntimeError):
    """The tenant does not have this source, or has it switched off."""


@dataclass
class LoadResult:
    seen: int = 0
    inserted: int = 0
    revised: int = 0
    unchanged: int = 0
    skipped: int = 0


def load_observations(
    conn: psycopg.Connection,
    source_code: str,
    raw_key: str,
    raw_sha256: str,
    observations: list[Observation],
    started_at: dt.datetime | None = None,
) -> LoadResult:
    started_at = started_at or dt.datetime.now(dt.timezone.utc)
    result = LoadResult(seen=len(observations))
    dates = [o.date for o in observations]
    range_from, range_to = (min(dates), max(dates)) if dates else (None, None)

    with conn.transaction(), conn.cursor() as cur:
        cur.execute("select is_enabled from rate_source where code = %s", (source_code,))
        row = cur.fetchone()
        if row is None or not row[0]:
            raise SourceNotEnabledError(f"Source '{source_code}' is not enabled for this tenant.")

        existing: dict[dt.date, Decimal] = {}
        if dates:
            cur.execute(
                "select rate_date, rate from fx_rate where source_code = %s and rate_date between %s and %s",
                (source_code, range_from, range_to),
            )
            existing = dict(cur.fetchall())

        new_rows: list[tuple] = []
        revisions: list[dict] = []
        for o in observations:
            if o.value is None:
                result.skipped += 1
                continue
            current = existing.get(o.date)
            if current is None:
                new_rows.append((source_code, o.date, o.value, raw_key))
            elif current != o.value:
                revisions.append({"date": o.date.isoformat(), "old": str(current), "new": str(o.value)})
            else:
                result.unchanged += 1

        if new_rows:
            cur.executemany(
                "insert into fx_rate (source_code, rate_date, rate, raw_ref) values (%s, %s, %s, %s)",
                new_rows,
            )
            result.inserted = len(new_rows)

        if revisions:
            cur.executemany(
                """
                update fx_rate set rate = %s, revision = revision + 1, raw_ref = %s, loaded_at = now()
                where source_code = %s and rate_date = %s
                """,
                [(Decimal(r["new"]), raw_key, source_code, dt.date.fromisoformat(r["date"])) for r in revisions],
            )
            result.revised = len(revisions)

        if result.inserted or result.revised:
            cur.execute(
                """
                insert into audit_log (actor, action, entity_type, entity_id, data)
                values (%s, 'rates.loaded', 'rate_source', %s, %s::jsonb)
                """,
                (
                    ACTOR,
                    source_code,
                    json.dumps({
                        "raw_ref": raw_key,
                        "inserted": result.inserted,
                        "revised": result.revised,
                        "revisions": revisions[:MAX_AUDITED_REVISIONS],
                    }),
                ),
            )

        _record(cur, source_code, raw_key, raw_sha256, range_from, range_to, result, "succeeded", None, started_at)
    return result


def record_failure(
    conn: psycopg.Connection, source_code: str, raw_key: str, raw_sha256: str, error: str, started_at: dt.datetime
) -> None:
    with conn.transaction(), conn.cursor() as cur:
        _record(cur, source_code, raw_key, raw_sha256, None, None, LoadResult(), "failed", error[:2000], started_at)


def _record(cur, source_code, raw_key, raw_sha256, range_from, range_to, r: LoadResult, status, error, started_at):
    cur.execute(
        """
        insert into rate_load (source_code, raw_ref, raw_sha256, range_from, range_to, rows_seen, rows_inserted,
                               rows_revised, rows_unchanged, rows_skipped, status, error, started_at)
        values (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
        """,
        (source_code, raw_key, raw_sha256, range_from, range_to, r.seen, r.inserted, r.revised, r.unchanged,
         r.skipped, status, error, started_at),
    )


def summary(r: LoadResult) -> str:
    return ", ".join(f"{k} {v}" for k, v in asdict(r).items())
