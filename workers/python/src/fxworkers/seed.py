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

    inserted = {"user_profile": 0}
    with conn.transaction(), conn.cursor() as cur:
        for u in dataset.users:
            cur.execute(
                """
                insert into user_profile (id, email, display_name, role)
                values (%s, %s, %s, %s) on conflict do nothing
                """,
                (u.id, u.email, u.display_name, u.role),
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

