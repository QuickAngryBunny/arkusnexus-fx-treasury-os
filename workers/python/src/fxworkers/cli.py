"""Command line entry point.

    fxworkers seed --tenant demo_norte      seed one dummy tenant
    fxworkers seed --all-dummy              seed every active dummy tenant
    fxworkers rates sync                    fetch the last 30 days of each source, load every tenant
    fxworkers rates backfill                fetch each source's full history, load every tenant
    fxworkers rates load --raw-ref KEY      replay a stored raw response (no network call)
    fxworkers rates schedule                run sync every business day after the FIX is published
    fxworkers rates requests                process pending "Sync now" requests once
    fxworkers forecast run                  publish forecasts and backtests for every tenant
    fxworkers curves sync|backfill          TIIE (Banxico) and US Treasury curve, every tenant
    fxworkers news sync                     flagged moves + GNews headlines (today and each move), every tenant
"""

from __future__ import annotations

import argparse
import sys


def _seed(args: argparse.Namespace) -> int:
    import psycopg

    from .control_plane import connect_tenant, control_plane_dsn, find_tenant, list_tenants
    import datetime as dt

    from .dummy_data import build_dataset
    from .dummy_treasury import build_treasury
    from .seed import fix_lookup, seed_tenant, seed_treasury

    with psycopg.connect(control_plane_dsn()) as cp:
        if args.all_dummy:
            tenants = [t for t in list_tenants(cp) if t.is_dummy and t.status == "active"]
        else:
            tenant = find_tenant(cp, args.tenant)
            if tenant is None:
                print(f"No provisioned tenant '{args.tenant}'.", file=sys.stderr)
                return 1
            tenants = [tenant]

        if not tenants:
            print("No active dummy tenants to seed.", file=sys.stderr)
            return 1

        for tenant in tenants:
            dataset = build_dataset(tenant.slug, tenant.display_name, user_count=args.users)
            with connect_tenant(cp, tenant) as conn:
                inserted = seed_tenant(conn, tenant, dataset)
                if args.treasury:
                    data = build_treasury(tenant.slug, until=dt.date.today(), rate_on=fix_lookup(conn))
                    inserted |= seed_treasury(conn, tenant, data)
            print(f"{tenant.slug}: inserted {inserted}")
    return 0


def _rates(args: argparse.Namespace) -> int:
    import datetime as dt

    import psycopg

    from .control_plane import control_plane_dsn, find_tenant
    from .rates.ingest import fetch_to_store, load_from_store
    from .rates.raw_store import open_store
    from .rates.sources import SOURCES, get_source

    sources = [get_source(args.source)] if args.source else list(SOURCES.values())
    store = open_store()
    with psycopg.connect(control_plane_dsn()) as cp:
        tenants = None
        if args.tenant:
            tenant = find_tenant(cp, args.tenant)
            if tenant is None:
                print(f"No provisioned tenant '{args.tenant}'.", file=sys.stderr)
                return 1
            tenants = [tenant]

        failures = 0
        for source in sources:
            if args.action == "load":
                key = args.raw_ref
            else:
                today = dt.date.today()
                start = source.first_date if args.action == "backfill" else (args.since or today - dt.timedelta(days=30))
                raw = fetch_to_store(store, source, max(start, source.first_date), today)
                print(f"{source.code}: stored {raw.key} ({len(raw.data):,} bytes)")
                key = raw.key
            failures += load_from_store(cp, store, source, key, tenants)
    return 1 if failures else 0


def _schedule(args: argparse.Namespace) -> int:
    import datetime as dt
    import time

    from .rates.schedule import Schedule, next_run

    schedule = Schedule(at=dt.time.fromisoformat(args.at), until=dt.time.fromisoformat(args.until),
                        retry=dt.timedelta(minutes=args.retry_minutes))
    done_for: dt.date | None = None
    print(f"Rate schedule: business days {schedule.at:%H:%M}-{schedule.until:%H:%M} {schedule.tz.key}, "
          f"retry every {args.retry_minutes} min.", flush=True)
    while True:
        now = dt.datetime.now(dt.timezone.utc)
        today = now.astimezone(schedule.tz).date()
        when = next_run(now, schedule, done_today=(done_for == today))
        wait = (when - now).total_seconds()
        try:
            _requests(argparse.Namespace())  # "Sync now" from the Full history page, checked every minute
        except Exception as e:
            print(f"Sync requests failed: {e}", file=sys.stderr, flush=True)
        if wait > 0:
            if int(wait) % 3600 < 60:
                print(f"Next sync at {when.isoformat(timespec='minutes')}", flush=True)
            time.sleep(min(wait, 60))
            continue
        print(f"Sync attempt at {now.isoformat(timespec='seconds')}", flush=True)
        try:
            ok = _rates(argparse.Namespace(action="sync", source=None, tenant=None, since=None)) == 0
            if ok and _have_fix_for(today):
                done_for = today
                try:
                    _curves(argparse.Namespace(action="sync", tenant=None))
                except Exception as e:  # forward model falls back to the last stored curve
                    print(f"Curve sync failed: {e}", file=sys.stderr, flush=True)
                if args.forecast:
                    _forecast(argparse.Namespace(tenant=None))
                try:
                    _news(argparse.Namespace(tenant=None, max_windows=20))
                except Exception as e:  # news is optional context; never block rates or forecasts
                    print(f"News sync failed: {e}", file=sys.stderr, flush=True)
                continue
        except Exception as e:  # keep the scheduler alive; the next attempt retries
            print(f"Sync failed: {e}", file=sys.stderr, flush=True)
        time.sleep(schedule.retry.total_seconds())


def _curves(args: argparse.Namespace) -> int:
    import datetime as dt

    import psycopg

    from .control_plane import control_plane_dsn, find_tenant
    from .rates.curves import CURVES_START, sync
    from .rates.raw_store import open_store

    today = dt.date.today()
    start = CURVES_START if args.action == "backfill" else today - dt.timedelta(days=30)
    with psycopg.connect(control_plane_dsn()) as cp:
        tenants = None
        if args.tenant:
            tenant = find_tenant(cp, args.tenant)
            if tenant is None:
                print(f"No provisioned tenant '{args.tenant}'.", file=sys.stderr)
                return 1
            tenants = [tenant]
        return 1 if sync(cp, open_store(), start, today, tenants) else 0


def _news(args: argparse.Namespace) -> int:
    import psycopg

    from .control_plane import control_plane_dsn, find_tenant, list_tenants
    from .news.sync import sync
    from .rates.raw_store import open_store

    with psycopg.connect(control_plane_dsn()) as cp:
        if args.tenant:
            tenant = find_tenant(cp, args.tenant)
            if tenant is None:
                print(f"No provisioned tenant '{args.tenant}'.", file=sys.stderr)
                return 1
            tenants = [tenant]
        else:
            tenants = [t for t in list_tenants(cp) if t.status == "active"]
        stats = sync(cp, open_store(), tenants, max_windows=args.max_windows)
    print(f"news: {stats['tenants']} tenants, {stats['moves']} flagged moves, {stats['windows_fetched']} windows fetched, "
          f"{stats['requests']} GNews requests", flush=True)
    return 0


def _forecast(args: argparse.Namespace) -> int:
    import psycopg

    from .control_plane import connect_tenant, control_plane_dsn, find_tenant, list_tenants
    from .forecast.publish import publish

    with psycopg.connect(control_plane_dsn()) as cp:
        if args.tenant:
            tenant = find_tenant(cp, args.tenant)
            if tenant is None:
                print(f"No provisioned tenant '{args.tenant}'.", file=sys.stderr)
                return 1
            tenants = [tenant]
        else:
            tenants = [t for t in list_tenants(cp) if t.status == "active"]
        failures = 0
        for tenant in tenants:
            try:
                with connect_tenant(cp, tenant) as conn:
                    r = publish(conn)
                print(f"{tenant.slug}: as of {r.as_of}, runs {r.runs_inserted}, backtests {r.backtests_inserted}",
                      flush=True)
            except Exception as e:
                failures += 1
                print(f"{tenant.slug}: FAILED ({e})", file=sys.stderr, flush=True)
    return 1 if failures else 0


def _requests(args: argparse.Namespace) -> int:
    import psycopg

    from .control_plane import control_plane_dsn
    from .rates.raw_store import open_store
    from .rates.requests import process_pending

    with psycopg.connect(control_plane_dsn()) as cp:
        process_pending(cp, open_store())
    return 0


def _have_fix_for(day) -> bool:
    """True when every active tenant holds a FIX for `day` (published or not, the window then ends the day)."""
    import psycopg

    from .control_plane import connect_tenant, control_plane_dsn, list_tenants

    with psycopg.connect(control_plane_dsn()) as cp:
        for tenant in (t for t in list_tenants(cp) if t.status == "active"):
            with connect_tenant(cp, tenant) as conn:
                row = conn.execute("select max(rate_date) from fx_rate where source_code = 'banxico_fix'").fetchone()
                if row[0] is None or row[0] < day:
                    return False
    return True


def main(argv: list[str] | None = None) -> int:
    from .env import load_env

    load_env()
    parser = argparse.ArgumentParser(prog="fxworkers")
    sub = parser.add_subparsers(dest="command", required=True)

    seed = sub.add_parser("seed", help="Seed dummy company data into dummy tenants")
    target = seed.add_mutually_exclusive_group(required=True)
    target.add_argument("--tenant", help="Tenant slug")
    target.add_argument("--all-dummy", action="store_true", help="Every active dummy tenant")
    seed.add_argument("--users", type=int, default=10, help="Users per tenant (default 10)")
    seed.add_argument("--no-treasury", dest="treasury", action="store_false",
                      help="Skip dummy bank accounts and income/expense movements")
    seed.set_defaults(func=_seed)

    rates = sub.add_parser("rates", help="Exchange-rate ingestion")
    rates_sub = rates.add_subparsers(dest="action", required=True)
    for name, text in (("sync", "Fetch recent days (default 30) and load"),
                       ("backfill", "Fetch full history and load"),
                       ("load", "Load a stored raw response again")):
        p = rates_sub.add_parser(name, help=text)
        if name == "load":  # a raw response belongs to exactly one series
            p.add_argument("--source", required=True, help="Source code of the raw response")
        else:
            p.add_argument("--source", help="Source code (default: all known sources)")
        p.add_argument("--tenant", help="Only this tenant slug (default: every active tenant)")
        if name == "sync":
            p.add_argument("--since", type=lambda v: __import__("datetime").date.fromisoformat(v),
                           help="Fetch from this date (YYYY-MM-DD)")
        if name == "load":
            p.add_argument("--raw-ref", required=True, help="Raw-store key printed by sync/backfill")
        p.set_defaults(func=_rates)

    curves = sub.add_parser("curves", help="Short-term interest-rate curves (TIIE, US Treasury)")
    curves_sub = curves.add_subparsers(dest="action", required=True)
    for name, text in (("sync", "Fetch the last 30 days and load"), ("backfill", "Fetch since 2018 and load")):
        c = curves_sub.add_parser(name, help=text)
        c.add_argument("--tenant", help="Only this tenant slug (default: every active tenant)")
        c.set_defaults(func=_curves)

    news = sub.add_parser("news", help="USD/MXN news from GNews")
    news_sub = news.add_subparsers(dest="action", required=True)
    ns = news_sub.add_parser("sync", help="Flag big moves and load today's and each move's headlines")
    ns.add_argument("--tenant", help="Only this tenant slug (default: every active tenant)")
    ns.add_argument("--max-windows", type=int, default=20, help="News windows to fetch this run (2 requests each)")
    ns.set_defaults(func=_news)

    forecast = sub.add_parser("forecast", help="Exchange-rate forecasts")
    forecast_sub = forecast.add_subparsers(dest="action", required=True)
    fr = forecast_sub.add_parser("run", help="Publish forecasts and backtests from each tenant's rate history")
    fr.add_argument("--tenant", help="Only this tenant slug (default: every active tenant)")
    fr.set_defaults(func=_forecast)

    req = rates_sub.add_parser("requests", help='Process pending "Sync now" requests once')
    req.set_defaults(func=_requests)

    sched = rates_sub.add_parser("schedule", help="Run sync every business day after the FIX is published")
    sched.add_argument("--at", default="12:30", help="Window start, Mexico City time (default 12:30)")
    sched.add_argument("--until", default="18:00", help="Window end, Mexico City time (default 18:00)")
    sched.add_argument("--retry-minutes", type=int, default=30)
    sched.add_argument("--no-forecast", dest="forecast", action="store_false",
                       help="Do not refresh forecasts after a successful sync")
    sched.set_defaults(func=_schedule)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
