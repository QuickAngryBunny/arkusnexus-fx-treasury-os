"""Command line entry point.

    fxworkers seed --tenant demo_norte      seed one dummy tenant
    fxworkers seed --all-dummy              seed every active dummy tenant
    fxworkers rates sync                    fetch the last 30 days of each source, load every tenant
    fxworkers rates backfill                fetch each source's full history, load every tenant
    fxworkers rates load --raw-ref KEY      replay a stored raw response (no network call)
"""

from __future__ import annotations

import argparse
import sys


def _seed(args: argparse.Namespace) -> int:
    import psycopg

    from .control_plane import connect_tenant, control_plane_dsn, find_tenant, list_tenants
    from .dummy_data import build_dataset
    from .seed import seed_tenant

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

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
