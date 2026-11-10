"""Write a dummy dataset into one tenant database (idempotent)."""

from __future__ import annotations

import json

import psycopg

from .control_plane import Tenant
from .dummy_data import Dataset
from .dummy_treasury import TreasuryDataset

ACTOR = "seeder"


class NotADummyTenantError(RuntimeError):
    """The seeder only ever writes to tenants flagged is_dummy in the control plane."""


def seed_tenant(conn: psycopg.Connection, tenant: Tenant, dataset: Dataset) -> dict[str, int]:
    """Insert the dataset; rows that already exist (same deterministic id) are left alone.

    Returns the number of rows inserted per table.
    """
    if not tenant.is_dummy:
        raise NotADummyTenantError(f"Tenant '{tenant.slug}' holds real data; the dummy seeder will not touch it.")
    if dataset.slug != tenant.slug:
        raise ValueError(f"Dataset for '{dataset.slug}' cannot be seeded into tenant '{tenant.slug}'.")

    inserted = {"legal_entity": 0, "department": 0, "user_profile": 0}
    with conn.transaction(), conn.cursor() as cur:
        for le in dataset.legal_entities:
            cur.execute(
                """
                insert into legal_entity (id, name, tax_id, country, functional_currency)
                values (%s, %s, %s, %s, %s) on conflict do nothing
                """,
                (le.id, le.name, le.tax_id, le.country, le.functional_currency),
            )
            inserted["legal_entity"] += cur.rowcount
        for d in dataset.departments:
            cur.execute(
                "insert into department (id, name, legal_entity_id) values (%s, %s, %s) on conflict do nothing",
                (d.id, d.name, d.legal_entity_id),
            )
            inserted["department"] += cur.rowcount
        for u in dataset.users:
            cur.execute(
                """
                insert into user_profile (id, email, display_name, role, department_id)
                values (%s, %s, %s, %s, %s) on conflict do nothing
                """,
                (u.id, u.email, u.display_name, u.role, u.department_id),
            )
            inserted["user_profile"] += cur.rowcount

        cur.execute(
            """
            insert into audit_log (actor, action, entity_type, entity_id, data)
            values (%s, 'seed.applied', 'tenant', %s, %s::jsonb)
            """,
            (ACTOR, str(tenant.id), json.dumps({"inserted": inserted})),
        )
    return inserted


def fix_lookup(conn: psycopg.Connection):
    """FIX on or before a day, from the tenant's own rate history (load rates before seeding treasury)."""
    import bisect

    rows = conn.execute(
        "select rate_date, rate from fx_rate where source_code = 'banxico_fix' order by rate_date"
    ).fetchall()
    dates = [r[0] for r in rows]

    def rate_on(day):
        i = bisect.bisect_right(dates, day)
        return rows[i - 1] if i else None

    return rate_on


def seed_treasury(conn: psycopg.Connection, tenant: Tenant, data: "TreasuryDataset") -> dict[str, int]:
    """Insert dummy bank accounts and their movements; existing rows are left alone.

    The dummy feed counts as a bank sync: connected accounts get last_synced_at = now.
    """
    from .dummy_data import _id as entity_id

    if not tenant.is_dummy:
        raise NotADummyTenantError(f"Tenant '{tenant.slug}' holds real data; the dummy seeder will not touch it.")
    if data.slug != tenant.slug:
        raise ValueError(f"Treasury data for '{data.slug}' cannot be seeded into tenant '{tenant.slug}'.")

    inserted = {"bank_account": 0, "financial_transaction": 0}
    with conn.transaction(), conn.cursor() as cur:
        for a in data.accounts:
            cur.execute(
                """
                insert into bank_account (id, legal_entity_id, bank_name, account_name, account_last4, currency, kind,
                                          opening_balance, opening_balance_date, feed_provider, feed_status)
                values (%s, %s, %s, %s, %s, %s, %s, %s, %s, 'dummy', 'connected') on conflict do nothing
                """,
                (a.id, entity_id(data.slug, "legal_entity", a.legal_entity_key), a.bank_name, a.account_name,
                 a.account_last4, a.currency, a.kind, a.opening_balance, a.opening_balance_date),
            )
            inserted["bank_account"] += cur.rowcount

        before = cur.execute("select count(*) from financial_transaction").fetchone()[0]
        cur.executemany(
            """
            insert into financial_transaction (id, bank_account_id, booked_on, direction, amount, currency, category,
                                               counterparty, description, source, conversion_id, counter_amount,
                                               counter_currency, fx_rate, fx_rate_source, fx_rate_at)
            values (%s, %s, %s, %s, %s, %s, %s, %s, %s, 'dummy_feed', %s, %s, %s, %s, %s, %s) on conflict do nothing
            """,
            [(t.id, t.bank_account_id, t.booked_on, t.direction, t.amount, t.currency, t.category, t.counterparty,
              t.description, t.conversion_id, t.counter_amount, t.counter_currency, t.fx_rate, t.fx_rate_source,
              t.fx_rate_at) for t in data.transactions],
        )
        inserted["financial_transaction"] = cur.execute("select count(*) from financial_transaction").fetchone()[0] - before

        cur.execute("update bank_account set last_synced_at = now() where feed_provider = 'dummy' and feed_status = 'connected'")
        if any(inserted.values()):
            cur.execute(
                """
                insert into audit_log (actor, action, entity_type, entity_id, data)
                values (%s, 'seed.treasury_applied', 'tenant', %s, %s::jsonb)
                """,
                (ACTOR, str(tenant.id), json.dumps({"inserted": inserted})),
            )
    return inserted
