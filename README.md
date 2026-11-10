# FX Intelligence & Treasury OS

Enterprise financial intelligence platform that combines historical currency data, predictive modeling, treasury
management, cash flow forecasting, profitability analysis and AI-powered financial decision support.
Built first for Arkus Nexus (USD/MXN), later as multi-tenant SaaS.

> **Phase 0 — internal only.** Dummy tenants and dummy data, no sign-in. The API refuses to start outside the
> `Development`, `Testing` and `Internal` environments. Sign-in and MFA arrive in Phase 3, before any real data.

## Architecture in one paragraph

A Next.js web app talks only to a .NET 10 API (modular monolith). The API resolves the tenant on each request and
opens **only that tenant's own PostgreSQL + TimescaleDB database** ([ADR 0001](docs/decisions/0001-database-per-tenant.md)).
A small control-plane database (`fx_control`) holds tenants and database routing. Python workers seed dummy data now
and load exchange rates from Phase 1.

```
apps/web/                 Next.js 15 app (App Router, TypeScript, Manrope, ES/EN)
src/FxOs.Api/             .NET 10 REST API (minimal APIs)
src/FxOs.Platform/        Tenancy, provisioning, migrations, secrets, audit
src/FxOs.Migrator/        CLI: migrate control plane / all tenants, provision dummy tenants
tests/FxOs.Api.Tests/     xUnit + Testcontainers: tenant isolation, provisioning, audit
db/control/  db/tenant/   Versioned SQL migrations (V0001__name.sql), embedded into FxOs.Platform
workers/python/           fxworkers: dummy-data seeder (Phase 0), rate ingestion (Phase 1)
docs/decisions/           Architecture decision records
```

## Run it locally

Prerequisites: .NET SDK 10, Node 22 + pnpm 10, Python 3.12+, Docker.

```bash
# 1. Postgres (TimescaleDB image) + Redis
docker compose up -d

# 2. Control plane + two dummy tenants (each gets its own database and login)
dotnet run --project src/FxOs.Migrator -- control
dotnet run --project src/FxOs.Migrator -- provision demo_norte "Demo Norte"
dotnet run --project src/FxOs.Migrator -- provision demo_bajio "Demo Bajío"

# 3. Dummy company data
cd workers/python && pip install -e ".[dev]" && fxworkers seed --all-dummy && cd ../..

# 4. API on http://localhost:5080
dotnet run --project src/FxOs.Api

# 5. Web on http://localhost:3000 — pick a tenant in the top-right switcher
cp apps/web/.env.example apps/web/.env.local
pnpm install && pnpm dev:web
```

Try the API directly:

```bash
curl -H "X-Tenant: demo_norte" http://localhost:5080/api/tenant/profile
curl -H "X-Tenant: demo_bajio" http://localhost:5080/api/tenant/departments
```

## Tests

```bash
dotnet test FxOs.slnx                 # needs Docker (Testcontainers)
cd workers/python && pytest -q
pnpm --filter @fxos/web typecheck
```

`TenantIsolationTests` is the Phase 0 exit criterion: two tenants with separate databases, cross-tenant logins
refused, a database belonging to another tenant refused by the connection factory, and every `/api/tenant/*` route
rejected without a tenant.

## Rules that keep tenants apart

- Tenant slugs are validated (`^[a-z][a-z0-9_]{2,30}$`) before they become database or role names.
- `CONNECT` on each tenant database is revoked from `PUBLIC`; only that tenant's login can connect.
- Every tenant connection checks `tenant_settings.tenant_id` against the tenant in context.
- `audit_log` is append-only (triggers block update, delete and truncate).
- Applied migrations are checksummed; editing one fails the next run. Add a new `V000N__` file instead.
- The seeder refuses tenants that are not flagged `is_dummy`.

## Phase 0 progress (3 one-week sprints)

- [x] **Sprint 1** — monorepo; control-plane DB; tenant provisioning (database, login, migrations, routing);
  dummy-data seeder; developer tenant switcher; audit log writer; isolation tests; CI.
- [ ] **Sprint 2** — Violeta component library in `packages/ui` (Button, Badge, Field, Switch, Segmented, StatCard,
  AlertBanner, PageHeader, Breadcrumb, AppNav with user menu) and the app shell; message catalogs for ES/EN.
- [ ] **Sprint 3** — Azure environments (internal network only), deploy workflow with per-tenant migrations,
  OpenTelemetry tagged with tenant id, Grafana dashboards, split migration owner vs app role.

The build plan lives in the project docs ("FX Intelligence & Treasury OS — Web App Build Plan").
