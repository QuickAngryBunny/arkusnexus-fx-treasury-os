"""Fetch news windows once (raw store), then load flagged moves and headlines into every tenant.

Windows: "today" (since midnight Mexico City) and one per flagged move (6 pm the day before to noon the day after).
A move window already stored is never fetched again; dates older than the plan reaches are not requested.
"""

from __future__ import annotations

import datetime as dt
import json
import sys
from dataclasses import dataclass
from zoneinfo import ZoneInfo

import psycopg

from ..control_plane import Tenant, connect_tenant
from ..rates.raw_store import RawStore, put_raw
from . import gnews, moves

MEXICO_CITY = ZoneInfo("America/Mexico_City")
SOURCE = "banxico_fix"
ACTOR = "news-ingestion"
TODAY_REFRESH = dt.timedelta(hours=3)


@dataclass(frozen=True)
class Window:
    key: str
    kind: str
    rate_date: dt.date
    start: dt.datetime
    end: dt.datetime


def move_window(day: dt.date) -> Window:
    start = dt.datetime.combine(day - dt.timedelta(days=1), dt.time(18, 0), MEXICO_CITY)
    end = dt.datetime.combine(day + dt.timedelta(days=1), dt.time(12, 0), MEXICO_CITY)
    return Window(f"move:{day.isoformat()}", "move", day, start, end)


def today_window(now: dt.datetime, plan: gnews.Plan) -> Window:
    """Since midnight Mexico City; with a delayed plan, the latest 24 hours it can see."""
    local = now.astimezone(MEXICO_CITY)
    end = now - dt.timedelta(hours=plan.delay_hours)
    start = dt.datetime.combine(local.date(), dt.time(0, 0), MEXICO_CITY)
    if end - start < dt.timedelta(hours=6):
        start = end - dt.timedelta(hours=24)
    return Window(f"today:{local.date().isoformat()}", "today", local.date(), start, end)


def load_rates(conn: psycopg.Connection) -> tuple[list[dt.date], list[float]]:
    rows = conn.execute("select rate_date, rate from fx_rate where source_code = %s order by rate_date", (SOURCE,)).fetchall()
    return [r[0] for r in rows], [float(r[1]) for r in rows]


def store_moves(conn: psycopg.Connection, flagged: list[moves.Move]) -> None:
    with conn.transaction():
        conn.execute("delete from rate_move where source_code = %s", (SOURCE,))
        with conn.cursor() as cur:
            cur.executemany(
                """insert into rate_move (source_code, rate_date, rate, change_pct, sigma, vol30_pct)
                   values (%s, %s, %s, %s, %s, %s)""",
                [(SOURCE, m.date, round(m.rate, 6), round(m.change_pct, 4), round(m.sigma, 2), round(m.vol30_pct, 2)) for m in flagged],
            )


def store_window(conn: psycopg.Connection, w: Window, articles: list[gnews.Article], raw_refs: list[str]) -> None:
    with conn.transaction(), conn.cursor() as cur:
        cur.execute("delete from news_window where window_key = %s", (w.key,))  # cascade: replaces today's headlines
        cur.execute(
            """insert into news_window (window_key, kind, rate_date, window_from, window_to, raw_refs, article_count)
               values (%s, %s, %s, %s, %s, %s, %s)""",
            (w.key, w.kind, w.rate_date, w.start, w.end, raw_refs, len(articles)),
        )
        cur.executemany(
            """insert into news_article (window_key, rank, lang, title, description, url, source_name, source_url, published_at)
               values (%s, %s, %s, %s, %s, %s, %s, %s, %s) on conflict do nothing""",
            [(w.key, i + 1, a.lang, a.title, a.description, a.url, a.source_name, a.source_url, a.published_at)
             for i, a in enumerate(articles)],
        )
        cur.execute(
            """insert into audit_log (actor, action, entity_type, entity_id, data)
               values (%s, 'news.loaded', 'news_window', %s, %s::jsonb)""",
            (ACTOR, w.key, json.dumps({"articles": len(articles), "raw_refs": raw_refs})),
        )


def sync(cp: psycopg.Connection, store: RawStore, tenants: list[Tenant], client: gnews.Client | None = None,
         plan: gnews.Plan | None = None, now: dt.datetime | None = None, max_windows: int = 20) -> dict[str, int]:
    plan = plan or gnews.Plan.from_env()
    now = now or dt.datetime.now(dt.timezone.utc)
    earliest = plan.earliest(now.astimezone(MEXICO_CITY).date())
    stats = {"tenants": 0, "windows_fetched": 0, "requests": 0, "moves": 0}

    # 1. Flagged moves per tenant (from its own rates), and which windows each tenant still lacks.
    needed: dict[str, Window] = {}
    for tenant in tenants:
        with connect_tenant(cp, tenant) as conn:
            dates, rates = load_rates(conn)
            flagged = moves.flagged(dates, rates)
            store_moves(conn, flagged)
            stats["moves"] = max(stats["moves"], len(flagged))
            have = {r[0]: r[1] for r in conn.execute("select window_key, fetched_at from news_window").fetchall()}
        today = today_window(now, plan)
        if today.key not in have or now - have[today.key] > TODAY_REFRESH:
            needed.setdefault(today.key, today)
        for m in flagged:
            w = move_window(m.date)
            if m.date >= earliest and w.key not in have:
                needed.setdefault(w.key, w)
        stats["tenants"] += 1

    # 2. Fetch each window once (both languages), newest first, within the run's request budget.
    client = client or gnews.Client()
    fetched: list[tuple[Window, list[gnews.Article], list[str]]] = []
    for w in sorted(needed.values(), key=lambda w: (w.kind != "today", -w.rate_date.toordinal()))[:max_windows]:
        by_lang: dict[str, list[gnews.Article]] = {}
        refs: list[str] = []
        try:
            for lang in ("es", "en"):
                raw = client.search(lang, w.start, w.end, plan.max_articles)
                obj = put_raw(store, "gnews", f"{w.key.replace(':', '_')}_{lang}", raw,
                              {"provider": "gnews", "window": w.key, "lang": lang})
                refs.append(obj.key)
                by_lang[lang] = gnews.parse(raw, lang)
        except gnews.QuotaExceeded as e:
            print(f"news: stopping, {e}", file=sys.stderr, flush=True)
            break
        except gnews.GNewsError as e:
            print(f"news: {w.key} failed ({e})", file=sys.stderr, flush=True)
            continue
        fetched.append((w, gnews.interleave(by_lang, plan.max_articles), refs))
    stats["requests"] = client.requests
    stats["windows_fetched"] = len(fetched)

    # 3. Copy into every tenant.
    for tenant in tenants:
        with connect_tenant(cp, tenant) as conn:
            for w, articles, refs in fetched:
                store_window(conn, w, articles, refs)
    return stats
