"""Read tenant routing from the control plane and open tenant connections.

Mirrors FxOs.Platform: tenant databases are reached only through the routing in the control plane,
and every connection checks that the database belongs to the expected tenant.
"""

from __future__ import annotations

import os
import uuid
from dataclasses import dataclass

import psycopg
from psycopg.conninfo import make_conninfo

DEFAULT_CONTROL_PLANE = "host=localhost port=5432 dbname=fx_control user=postgres password=postgres"
DEFAULT_TENANT_SERVER = "host=localhost port=5432 user=postgres password=postgres"


class TenantIsolationError(RuntimeError):
    """Raised when a database does not belong to the tenant it was opened for."""


@dataclass(frozen=True)
class Tenant:
    id: uuid.UUID
    slug: str
    display_name: str
    status: str
    is_dummy: bool
    server_key: str
    database_name: str
    login_role: str
    secret_ref: str


def control_plane_dsn() -> str:
    return os.environ.get("FX_CONTROL_PLANE_DSN", DEFAULT_CONTROL_PLANE)


def tenant_server_dsn(server_key: str) -> str:
    env = f"FX_TENANT_SERVER_{server_key.upper()}"
    if server_key == "default":
        return os.environ.get(env, DEFAULT_TENANT_SERVER)
    value = os.environ.get(env)
    if not value:
        raise RuntimeError(f"Tenant server '{server_key}' is not configured ({env}).")
    return value


_SELECT = """
    select t.id, t.slug, t.display_name, t.status, t.is_dummy,
           d.server_key, d.database_name, d.login_role, d.secret_ref
    from tenant t
    join tenant_database d on d.tenant_id = t.id
"""


def list_tenants(cp: psycopg.Connection) -> list[Tenant]:
    with cp.cursor() as cur:
        cur.execute(_SELECT + " order by t.slug")
        return [Tenant(*row) for row in cur.fetchall()]


def find_tenant(cp: psycopg.Connection, slug: str) -> Tenant | None:
    with cp.cursor() as cur:
        cur.execute(_SELECT + " where t.slug = %s", (slug,))
        row = cur.fetchone()
        return Tenant(*row) if row else None


def tenant_password(cp: psycopg.Connection, tenant: Tenant) -> str:
    # Phase 0: dev secret store in the control plane. Key Vault replaces this outside internal environments.
    with cp.cursor() as cur:
        cur.execute("select secret_value from dev_secret where secret_ref = %s", (tenant.secret_ref,))
        row = cur.fetchone()
    if row is None:
        raise RuntimeError(f"Secret '{tenant.secret_ref}' for tenant '{tenant.slug}' not found.")
    return row[0]


def connect_tenant(cp: psycopg.Connection, tenant: Tenant) -> psycopg.Connection:
    dsn = make_conninfo(
        tenant_server_dsn(tenant.server_key),
        dbname=tenant.database_name,
        user=tenant.login_role,
        password=tenant_password(cp, tenant),
        application_name="fxworkers",
    )
    conn = psycopg.connect(dsn)
    try:
        with conn.cursor() as cur:
            cur.execute("select tenant_id from tenant_settings where id = 1")
            row = cur.fetchone()
        if row is None or row[0] != tenant.id:
            raise TenantIsolationError(
                f"Database '{tenant.database_name}' does not belong to tenant '{tenant.slug}'."
            )
        return conn
    except BaseException:
        conn.close()
        raise
