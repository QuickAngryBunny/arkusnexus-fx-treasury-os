"""Loader against a real TimescaleDB. Runs when FX_TEST_PG_DSN points at a server where it may create databases
(CI provides one); skipped otherwise. Applies the real tenant migrations, so it also checks they run."""

import datetime as dt
import os
import uuid
from decimal import Decimal
from pathlib import Path

import psycopg
import pytest
from psycopg.conninfo import make_conninfo

from fxworkers.rates.banxico import Observation
from fxworkers.rates.loader import SourceNotEnabledError, load_observations

DSN = os.environ.get("FX_TEST_PG_DSN")
MIGRATIONS = Path(__file__).resolve().parents[3] / "db" / "tenant"
pytestmark = pytest.mark.skipif(not DSN, reason="FX_TEST_PG_DSN not set")


@pytest.fixture
def tenant_db():
    name = f"fx_test_{uuid.uuid4().hex[:10]}"
    with psycopg.connect(DSN, autocommit=True) as admin:
        admin.execute(f'create database "{name}"')
    try:
        with psycopg.connect(make_conninfo(DSN, dbname=name)) as conn:
            for path in sorted(MIGRATIONS.glob("V*.sql")):
                conn.execute(path.read_text(encoding="utf-8"))
            conn.execute("insert into tenant_settings (id, tenant_id, display_name) values (1, %s, 'Test')",
                         (uuid.uuid4(),))
            conn.commit()
            yield conn
    finally:
        with psycopg.connect(DSN, autocommit=True) as admin:
            admin.execute(f'drop database if exists "{name}" with (force)')


def obs(*pairs):
    return [Observation(dt.date.fromisoformat(d), None if v is None else Decimal(v)) for d, v in pairs]


def rates(conn):
    return conn.execute("select rate_date::text, rate, revision from fx_rate order by rate_date").fetchall()


def test_rerunning_a_load_changes_nothing(tenant_db):
    data = obs(("2026-11-09", "18.70"), ("2026-11-10", "18.6678"), ("2026-11-11", None))
    first = load_observations(tenant_db, "banxico_fix", "raw/a.json", "a" * 64, data)
    before = rates(tenant_db)
    second = load_observations(tenant_db, "banxico_fix", "raw/a.json", "a" * 64, data)

    assert (first.inserted, first.skipped) == (2, 1)
    assert (second.inserted, second.revised, second.unchanged) == (0, 0, 2)
    assert rates(tenant_db) == before
    audits = tenant_db.execute("select count(*) from audit_log where action = 'rates.loaded'").fetchone()[0]
    loads = tenant_db.execute("select count(*) from rate_load where status = 'succeeded'").fetchone()[0]
    assert (audits, loads) == (1, 2)


def test_a_revised_value_is_replaced_versioned_and_audited(tenant_db):
    load_observations(tenant_db, "banxico_fix", "raw/a.json", "a" * 64, obs(("2026-11-10", "18.6678")))
    result = load_observations(tenant_db, "banxico_fix", "raw/b.json", "b" * 64, obs(("2026-11-10", "18.6700")))

    assert result.revised == 1
    assert rates(tenant_db) == [("2026-11-10", Decimal("18.670000"), 2)]
    revision = tenant_db.execute(
        "select data->'revisions'->0 from audit_log where action = 'rates.loaded' order by id desc limit 1"
    ).fetchone()[0]
    assert revision == {"date": "2026-11-10", "old": "18.667800", "new": "18.6700"}


def test_a_disabled_source_is_refused(tenant_db):
    tenant_db.execute("update rate_source set is_enabled = false")
    with pytest.raises(SourceNotEnabledError):
        load_observations(tenant_db, "banxico_fix", "raw/a.json", "a" * 64, obs(("2026-11-10", "18.6")))
