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
apps/web/                 Next.js 15 app (App Router, TypeScript, Manrope, ES/EN catalogs in messages/)
packages/ui/              @fxos/ui — Violeta component library (tokens, components, styles.css)
src/FxOs.Api/             .NET 10 REST API (minimal APIs)
src/FxOs.Platform/        Tenancy, provisioning, migrations, secrets, audit
src/FxOs.Migrator/        CLI: migrate control plane / all tenants, provision dummy tenants
tests/FxOs.Api.Tests/     xUnit + Testcontainers: tenant isolation, provisioning, audit
db/control/  db/tenant/   Versioned SQL migrations (V0001__name.sql), embedded into FxOs.Platform
workers/python/           fxworkers: dummy-data seeder (Phase 0), Banxico rate ingestion (Phase 1)
docs/decisions/           Architecture decision records
```

## Run it locally

Prerequisites: .NET SDK 10, Node 22 + pnpm 10, Python 3.12+, Docker.

```bash
# 1. Postgres (TimescaleDB image), Azurite (raw store) and Redis
docker compose up -d

# 2. Control plane + two dummy tenants (each gets its own database and login)
dotnet run --project src/FxOs.Migrator -- control
dotnet run --project src/FxOs.Migrator -- provision demo_norte "Demo Norte"
dotnet run --project src/FxOs.Migrator -- provision demo_bajio "Demo Bajío"

# 3. Worker install (rates and forecasts need workers/python/.env with BANXICO_TOKEN; see .env.example)
cd workers/python && python3 -m venv .venv && .venv/bin/pip install -e ".[dev]" && cd ../..

# 3b. Real rates first (dummy conversions use the FIX), then dummy company + treasury data, then forecasts
cd workers/python && .venv/bin/fxworkers rates backfill && .venv/bin/fxworkers curves backfill \
  && .venv/bin/fxworkers seed --all-dummy && .venv/bin/fxworkers forecast run && cd ../..

# 4. API on http://localhost:5080
dotnet run --project src/FxOs.Api

# 5. Web on http://localhost:3000 — pick a tenant in the top-right switcher
cp apps/web/.env.example apps/web/.env.local
pnpm install && pnpm dev:web
```

After the first setup, start everything with one command (also after a restart):

```bash
./scripts/dev.sh     # Docker, API (.logs/api.log), rate scheduler (.logs/rates.log) and web; Ctrl+C stops them
```

Try the API directly:

```bash
curl -H "X-Tenant: demo_norte" http://localhost:5080/api/tenant/profile
curl -H "X-Tenant: demo_bajio" http://localhost:5080/api/tenant/departments
```

## Exchange rates (Phase 1)

Only the ingestion worker calls a rate source; the API and web read each tenant's own copy, so history stays
readable with every connector switched off.

```
Banxico SIE (SF43718, FIX) ──fetch once──▶ raw store (Blob/Azurite, immutable, keyed by time + sha256)
                                              │ parse + validate
                                              ▼
                         every active tenant DB: fx_rate (hypertable) · rate_load (log) · audit_log
```

```bash
fxworkers rates backfill                    # full history (about 8,800 days), every tenant
fxworkers rates sync [--since 2026-10-01]   # last 30 days by default; picks up revisions
fxworkers rates load --source banxico_fix --raw-ref <key>   # replay a stored response, no network
fxworkers rates schedule                    # business days 12:30–18:00 Mexico City, retries every 30 min,
                                            # then syncs curves and refreshes forecasts (started by ./scripts/dev.sh)
fxworkers curves backfill|sync              # TIIE 28/91/182 (Banxico) + US Treasury 1M–1Y curve, since 2018
fxworkers rates requests                    # process pending "Sync now" / "Re-fetch" requests once
                                            # (the scheduler also does this every minute)
```

- A rerun changes no rates. A value the source revises replaces the old one, bumps `revision` and is audited.
- `--tenant <slug>` limits any command to one tenant. A tenant without the source enabled is skipped.
- API: `GET /api/tenant/rates/sources`, `/api/tenant/rates/{source}/latest`,
  `/api/tenant/rates/{source}?from=YYYY-MM-DD&to=YYYY-MM-DD` (default: 90 days to the latest rate).
- The token lives only in `workers/python/.env` (git-ignored); it is sent as the `Bmx-Token` header, never stored.

## Dummy treasury data (Phases 0–2)

`fxworkers seed --all-dummy` also writes three connected dummy bank accounts per tenant (US LLC operating USD,
Mexican S.A. operating MXN, Mexican S.A. USD) and their movements since 2025-01-01: US client collections, a monthly
intercompany transfer to Mexico, MXN payroll (with December aguinaldo), IMSS, SAT, rent and utilities, local MXN
clients, and a monthly USD→MXN conversion at the stored FIX minus a bank spread. Both conversion legs keep their
original amount and the applied rate, its source and time. Values are deterministic per day, so a later run only adds
new days. API: `/api/tenant/bank-accounts`, `/api/tenant/transactions`, `/api/tenant/cash-flow/monthly` (intercompany
and conversions reported as transfers, not income or expense).

## Forecasts (Phase 2)

`fxworkers forecast run` publishes, per tenant and from its own rate history, 7/15/30/60/90/180-day forecasts with
80% and 95% intervals for every registered model, plus a walk-forward backtest since 2019 against the naive benchmark
(`forecast_model`, `forecast_run`, `forecast_point`, `backtest_result`; a rerun for the same as-of date changes
nothing). Models, in display order: `forward` (market forward from interest-rate parity: FIX × (1 + TIIE·d/360) /
(1 + US Treasury·d/365), tenor interpolated, with an EWMA volatility band), `ewma_vol` (point = today's FIX,
volatility-adaptive band) and `naive` (benchmark). Backtests since 2019 show no model beats naive on the point (the
forward misses by 1–17% more), so the page presents the forward as today's price to lock in a rate, not a prediction.
A drift variant was tried and dropped. API: `/api/tenant/rates/{source}/analytics` (trend, volatility, 52-week range,
changes) and `/api/tenant/forecasts/{source}` (models in `forecast_model.display_order`).

## Violeta UI (`packages/ui`)

`@fxos/ui` ships TypeScript source that Next compiles (`transpilePackages`). Import the styles once in the root
layout (`import "@fxos/ui/styles.css"`) and the components from `@fxos/ui`. Tokens (`--vi-*`) come from the design
canvas's "Violeta design system" artboard. Components that render links take `linkAs={Link}` for client navigation.
With the developer switcher on, `/ui` shows every component (open it from the user menu → Development).

Text lives in `apps/web/messages/es-MX.json` and `en-US.json`. The language comes from the user menu (cookie
`fx_locale`), else the tenant's default locale, else Spanish.

## Tests

```bash
dotnet test FxOs.slnx                 # needs Docker (Testcontainers)
cd workers/python && pytest -q        # loader tests also need FX_TEST_PG_DSN (any Postgres+TimescaleDB superuser DSN)
pnpm -r typecheck                     # packages/ui + apps/web; also checks ES/EN catalogs have the same keys
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
- [x] **Sprint 2** — Violeta component library in `packages/ui` (Button, Badge, Field, Switch, Segmented, StatCard,
  AlertBanner, PageHeader, Breadcrumb, AppNav with user menu) and the app shell; message catalogs for ES/EN.
- [ ] **Sprint 3** — Azure environments (internal network only), deploy workflow with per-tenant migrations,
  OpenTelemetry tagged with tenant id, Grafana dashboards, split migration owner vs app role.

The build plan lives in the project docs ("FX Intelligence & Treasury OS — Web App Build Plan").
