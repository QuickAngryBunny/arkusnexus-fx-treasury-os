# ADR 0001 — One database per tenant

- Status: accepted
- Date: 2026-11-10
- Source: Web App Build Plan, "Decisions needed" (confirmed)

## Decision

Every tenant gets its own PostgreSQL (+ TimescaleDB) database with its own login role. The tenant database holds
all PRD §8 entities except `Tenant`, including its own copy of the public exchange-rate history, its data sources,
sync settings, corrections and forecasts.

A small **control-plane** database (`fx_control`) holds only what the app needs before it knows the tenant:
tenants, database routing and schema version, and (from Phase 3) login identities, memberships with roles,
invitations, password resets and sessions. There is no billing or subscription data.

## How it is enforced

1. `CONNECT` on each tenant database is revoked from `PUBLIC`; only that tenant's login role may connect.
2. The API resolves the tenant per request and only ever opens that tenant's connection pool.
3. Every tenant connection checks `tenant_settings.tenant_id` against the tenant in context and refuses a mismatch.
4. Automated tests prove 1–3 on every build (`tests/FxOs.Api.Tests/TenantIsolationTests.cs`).

## Consequences

- Migrations run per tenant database (`FxOs.Migrator tenants`); the control plane records each database's version.
- Public rate history is duplicated per tenant. Connectors fetch each source once into an immutable raw store
  and fan out per-tenant loads (Phase 1), so external APIs are not called once per tenant.
- Phase 0 keeps one login role per tenant that both migrates and serves requests. Before real data (Phase 3) this
  splits into a migration owner role and a least-privilege app role, so the app cannot drop the audit trigger.
