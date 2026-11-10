"""Command line entry point.

    fxworkers seed --tenant demo_norte      seed one dummy tenant
    fxworkers seed --all-dummy              seed every active dummy tenant
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="fxworkers")
    sub = parser.add_subparsers(dest="command", required=True)

    seed = sub.add_parser("seed", help="Seed dummy company data into dummy tenants")
    target = seed.add_mutually_exclusive_group(required=True)
    target.add_argument("--tenant", help="Tenant slug")
    target.add_argument("--all-dummy", action="store_true", help="Every active dummy tenant")
    seed.add_argument("--users", type=int, default=10, help="Users per tenant (default 10)")
    seed.set_defaults(func=_seed)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
