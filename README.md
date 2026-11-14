# FX Intelligence & Treasury OS

USD/MXN exchange-rate intelligence: rate history, forecasts and news (Phases 0–2), accounts and team (Phase 3),
conversion plans as the main action (Phase 4) and an AI assistant (Phase 5). Built first for Arkus Nexus, later as
multi-tenant SaaS. Treasury, exposure, cash flow, simulator, profitability, billing, self-serve sign-up and two-step
verification are out of scope ([Phase 3 brief](docs/phase-3-brief.md)).

> **Internal only until Phase 3.** Dummy tenants and dummy data, no sign-in. The API refuses to start outside the
> `Development`, `Testing` and `Internal` environments. Phase 3 adds the app's own email-and-password sign-in (no MFA,
> no outside identity provider) behind the private network, before any real data.

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

# 3b. Real rates, interest-rate curves, dummy users, then forecasts
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
curl -H "X-Tenant: demo_bajio" http://localhost:5080/api/tenant/users
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
fxworkers news sync                         # flag big moves (±2σ) and load GNews headlines for today and each move
fxworkers rates requests                    # process pending "Sync now" / "Re-fetch" requests once
                                            # (the scheduler also does this every minute)
```

- A rerun changes no rates. A value the source revises replaces the old one, bumps `revision` and is audited.
- `--tenant <slug>` limits any command to one tenant. A tenant without the source enabled is skipped.
- API: `GET /api/tenant/rates/sources`, `/api/tenant/rates/{source}/latest`,
  `/api/tenant/rates/{source}?from=YYYY-MM-DD&to=YYYY-MM-DD` (default: 90 days to the latest rate).
- The token lives only in `workers/python/.env` (git-ignored); it is sent as the `Bmx-Token` header, never stored.

## Dummy data (Phases 0–2)

`fxworkers seed --all-dummy` writes deterministic dummy users (`.example` emails) into every active dummy tenant.
It refuses any tenant not flagged `is_dummy`. Treasury data (bank accounts, transactions, legal entities, departments)
was removed in tenant migration V0008 when the product narrowed to exchange rates and conversion plans.

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

## News (Phase 2)

Exchange Forecast marks every **big move** (daily change beyond ±2σ of the previous 30 days) on the chart; tapping a
marker, or **Today**, shows the move's facts and the most relevant GNews headlines (Spanish from Mexican publishers,
English when available). `fxworkers news sync` (also run by the scheduler after forecasts) computes the moves from each
tenant's own rates, fetches each news window once (today since midnight Mexico City; a move from 6 pm the day before to
noon the day after), keeps the raw response in the raw store, and copies headlines into every tenant (`rate_move`,
`news_window`, `news_article`). `GET /api/tenant/news?from&to` reads only the tenant database, never GNews.

The key lives only in `workers/python/.env` (`GNEWS_API_KEY`), sent as the `X-Api-Key` header. The free plan reaches
the last 30 days with a 12-hour delay and 100 requests a day (1 per second): older moves keep their marker and say no
headlines are stored. Set `GNEWS_PLAN=paid` (history since 2020, real time) and run the sync again to fill them.

## Email through Firebase (Phase 3)

The API writes each email to the control-plane outbox (`email_outbox`, body encrypted with ASP.NET Data Protection)
in the same transaction as the change that sends it. A background dispatcher hands due emails to the Firebase
Trigger Email extension by creating a document in the Firestore `mail` collection (`{ to, message: { subject, html,
text } }`, document id = outbox id, so retries never send twice), then clears the stored body. Firebase only sends;
it does no sign-in and holds nothing but the email itself.

Local runs write to the **real** Firebase project (`fx-treasury`), so the dispatcher sends only to
`Email:AllowedRecipients` (exact addresses or `*@domain`); anything else is marked failed without reaching Firebase.

**Credentials: no key files.** The organization blocks service-account key creation, so the API uses Application
Default Credentials, impersonating the sender service account
`fxos-mail-sender@fx-treasury-514701.iam.gserviceaccount.com` (it lives in project `fx-treasury-514701` and has
"Cloud Datastore User" on `fx-treasury`). Your Google account needs "Service Account Token Creator" on it.

```bash
gcloud auth application-default login --impersonate-service-account=fxos-mail-sender@fx-treasury-514701.iam.gserviceaccount.com
cd src/FxOs.Api
dotnet user-secrets set "Email:Firebase:ProjectId" "fx-treasury"
dotnet user-secrets set "Email:AllowedRecipients:0" "you@yourcompany.com"
```

`Email:Firebase:CredentialsFile` stays empty (it exists for environments that do use a key). With
`Email:Dispatcher:Enabled` (on in Development) and a project id set, the API starts the dispatcher. Try it with
`curl -X POST localhost:5080/api/dev/email/test -H 'content-type: application/json' -d '{"to":"you@yourcompany.com"}'`,
then check `email_outbox.status` and the extension's `delivery.state` on the Firestore `mail` document.

**Firebase project setup** (`fx-treasury`, already done). The organization disables automatic IAM grants, so the
Trigger Email extension needed these by hand, all for `426189336240-compute@developer.gserviceaccount.com`:
"Cloud Build Service Account" on the project (function build), and "Cloud Run Invoker" on the service
`ext-firestore-send-email-hvmf-processqueue` (the Eventarc trigger calls it). Extension settings: Firestore `(default)`
in `nam5`, collection `mail`, SMTP via the sending Gmail account with an app password in Secret Manager.
For Azure (DevOps hand-off): use Workload Identity Federation for the sender account instead of a key.

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
