"""Write a dummy dataset into one tenant database (idempotent)."""

from __future__ import annotations

import json

import psycopg

from .control_plane import Tenant
from .dummy_data import Dataset

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

    inserted = {"user_profile": 0, "bank": 0}
    with conn.transaction(), conn.cursor() as cur:
        for u in dataset.users:
            cur.execute(
                """
                insert into user_profile (user_id, email, display_name)
                values (%s, %s, %s) on conflict do nothing
                """,
                (u.id, u.email, u.display_name),
            )
            inserted["user_profile"] += cur.rowcount
        for b in dataset.banks:
            # Skips names already in use in that currency (case-insensitive), e.g. one the admin typed.
            cur.execute(
                """
                insert into bank (id, name, currency) select %s, %s, %s
                where not exists (select 1 from bank where currency = %s and lower(name) = lower(%s) and removed_at is null)
                on conflict (id) do update set removed_at = null where bank.removed_at is not null
                """,
                (b.id, b.name, b.currency, b.currency, b.name),
            )
            inserted["bank"] += cur.rowcount

        cur.execute(
            """
            insert into audit_log (actor, action, entity_type, entity_id, data)
            values (%s, 'seed.applied', 'tenant', %s, %s::jsonb)
            """,
            (ACTOR, str(tenant.id), json.dumps({"inserted": inserted})),
        )
    return inserted



def clear_tenant(conn: psycopg.Connection, tenant: Tenant, dataset: Dataset) -> dict[str, int]:
    """Remove what seed_tenant added (by its stable ids); anything people entered themselves stays.

    Banks are removed softly; if onboarding is complete the database keeps the last bank of each currency.
    """
    if not tenant.is_dummy:
        raise NotADummyTenantError(f"Tenant '{tenant.slug}' holds real data; the dummy seeder will not touch it.")
    if dataset.slug != tenant.slug:
        raise ValueError(f"Dataset for '{dataset.slug}' cannot be cleared from tenant '{tenant.slug}'.")

    removed = {"user_profile": 0, "bank": 0}
    with conn.transaction(), conn.cursor() as cur:
        cur.execute("delete from user_profile where user_id = any(%s)", ([u.id for u in dataset.users],))
        removed["user_profile"] = cur.rowcount
        for b in dataset.banks:
            try:
                with conn.transaction():
                    cur.execute("update bank set removed_at = now() where id = %s and removed_at is null", (b.id,))
                    removed["bank"] += cur.rowcount
            except psycopg.errors.CheckViolation:
                pass  # last bank of a currency after onboarding: kept
        cur.execute(
            """
            insert into audit_log (actor, action, entity_type, entity_id, data)
            values (%s, 'seed.cleared', 'tenant', %s, %s::jsonb)
            """,
            (ACTOR, str(tenant.id), json.dumps({"removed": removed})),
        )
    return removed
