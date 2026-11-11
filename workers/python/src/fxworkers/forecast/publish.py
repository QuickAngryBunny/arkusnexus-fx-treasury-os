"""Compute forecasts and backtests from one tenant's own rate history and store them in its database."""

from __future__ import annotations

import datetime as dt
import json
from dataclasses import dataclass
from decimal import Decimal

import psycopg

from .backtest import backtest
from .models import HORIZONS, MODELS, Curves, CurveSeries, History, business_steps

ACTOR = "forecasting"
Q = Decimal("0.000001")


@dataclass
class PublishResult:
    as_of: dt.date | None = None
    runs_inserted: int = 0
    backtests_inserted: int = 0


def _d(value: float) -> Decimal:
    return Decimal(repr(value)).quantize(Q)


def load_history(conn: psycopg.Connection, source_code: str) -> History | None:
    rows = conn.execute(
        "select rate_date, rate from fx_rate where source_code = %s order by rate_date", (source_code,)
    ).fetchall()
    if len(rows) < 2:
        return None
    return History([r[0] for r in rows], [float(r[1]) for r in rows], load_curves(conn))


def load_curves(conn: psycopg.Connection) -> Curves:
    series: dict[str, CurveSeries] = {}
    by_currency: dict[str, list[CurveSeries]] = {}
    rows = conn.execute(
        """
        select s.code, s.currency, s.tenor_days, s.day_count, r.rate_date, r.rate_pct
        from interest_series s join interest_rate r on r.series_code = s.code
        where s.is_enabled order by s.code, r.rate_date
        """
    ).fetchall()
    for code, currency, tenor, basis, day, pct in rows:
        if code not in series:
            series[code] = CurveSeries(tenor, basis, [], [])
            by_currency.setdefault(currency.strip(), []).append(series[code])
        series[code].dates.append(day)
        series[code].rates_pct.append(float(pct))
    return Curves(by_currency)


def publish(conn: psycopg.Connection, source_code: str = "banxico_fix") -> PublishResult:
    """Idempotent: a forecast for an as-of date that is already stored is kept as it was."""
    result = PublishResult()
    h = load_history(conn, source_code)
    if h is None:
        return result
    i = len(h.dates) - 1
    as_of = result.as_of = h.dates[i]

    with conn.transaction(), conn.cursor() as cur:
        for m in MODELS:
            cur.execute(
                """
                insert into forecast_model (code, version, name, description, params, is_benchmark, display_order)
                values (%s, %s, %s, %s, %s::jsonb, %s, %s)
                on conflict (code, version) do update set display_order = excluded.display_order
                """,
                (m.code, m.version, m.name, m.description, json.dumps(m.params), m.is_benchmark, m.display_order),
            )

        naive_mae: dict[int, float] = {}
        scores = {m.code: {hz: backtest(h, m, hz) for hz in HORIZONS} for m in MODELS}
        for hz in HORIZONS:
            s = scores["naive"][hz]
            naive_mae[hz] = s.mae if s else float("nan")

        for m in MODELS:
            forecasts = {hz: m.forecast(h, i, business_steps(as_of, hz), hz) for hz in HORIZONS}
            if any(f is None for f in forecasts.values()):
                continue  # missing input (e.g. no curve) — nothing is published for this model today
            cur.execute(
                """
                insert into forecast_run (source_code, model_code, model_version, as_of_date, last_rate)
                values (%s, %s, %s, %s, %s) on conflict do nothing returning id
                """,
                (source_code, m.code, m.version, as_of, Decimal(str(h.rates[i]))),
            )
            row = cur.fetchone()
            if row is not None:
                result.runs_inserted += 1
                points = []
                for hz, f in forecasts.items():
                    points.append((row[0], hz, as_of + dt.timedelta(days=hz), _d(f.point), _d(f.lower80),
                                   _d(f.upper80), _d(f.lower95), _d(f.upper95)))
                cur.executemany(
                    """
                    insert into forecast_point (run_id, horizon_days, target_date, point, lower80, upper80,
                                                lower95, upper95) values (%s, %s, %s, %s, %s, %s, %s, %s)
                    """,
                    points,
                )

            for hz, s in scores[m.code].items():
                if s is None:
                    continue
                cur.execute(
                    """
                    insert into backtest_result (source_code, model_code, model_version, horizon_days, origins_from,
                        origins_to, n, mae, mape, coverage80, coverage95, mae_vs_naive)
                    values (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s) on conflict do nothing
                    """,
                    (source_code, m.code, m.version, hz, s.origins_from, s.origins_to, s.n, _d(s.mae),
                     _d(s.mape), round(s.coverage80, 4), round(s.coverage95, 4), _d(s.mae / naive_mae[hz])),
                )
                result.backtests_inserted += cur.rowcount

        if result.runs_inserted:
            cur.execute(
                """
                insert into audit_log (actor, action, entity_type, entity_id, data)
                values (%s, 'forecast.published', 'rate_source', %s, %s::jsonb)
                """,
                (ACTOR, source_code, json.dumps({"as_of": as_of.isoformat(), "models": [m.code for m in MODELS]})),
            )
    return result
