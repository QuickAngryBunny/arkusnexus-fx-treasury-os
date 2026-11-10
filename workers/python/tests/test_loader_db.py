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


def test_forecasts_publish_once_per_as_of_date_with_backtests(tenant_db):
    import math as _m
    import random as _r

    from fxworkers.forecast.publish import publish

    rng, log, day, rows = _r.Random(3), _m.log(17.0), dt.date(2012, 1, 2), []
    while day <= dt.date(2026, 11, 10):
        if day.weekday() < 5:
            log += rng.gauss(0, 0.005)
            rows.append(Observation(day, Decimal(str(round(_m.exp(log), 4)))))
        day += dt.timedelta(days=1)
    load_observations(tenant_db, "banxico_fix", "raw/rw.json", "c" * 64, rows)

    first = publish(tenant_db)
    second = publish(tenant_db)
    assert (first.runs_inserted, second.runs_inserted, second.backtests_inserted) == (2, 0, 0)
    points = tenant_db.execute("select count(*) from forecast_point").fetchone()[0]
    benchmark = tenant_db.execute(
        "select min(mae_vs_naive), max(mae_vs_naive) from backtest_result where model_code = 'naive'"
    ).fetchone()
    assert points == 12 and benchmark == (Decimal("1.000000"), Decimal("1.000000"))
