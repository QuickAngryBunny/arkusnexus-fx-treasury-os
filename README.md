# FX Intelligence & Treasury OS

A USD/MXN exchange-rate platform for companies that convert between pesos and dollars. Built first for Arkus Nexus,
designed as multi-tenant SaaS: every company gets its own PostgreSQL database.

## What it does today

- **Exchange Forecast**: today's Banxico FIX, trend and volatility; 7- to 180-day forecasts with 80% and 95% ranges;
  a chart of the past against the forecast, including how far earlier forecasts missed; big daily moves with the news
  that explains them.
- **Full history**: every FIX since 1991 by year, month and day, with gap detection, holiday marking, manual sync and
  CSV export.
- **Rate alerts**: an email when the FIX rises above or falls below a rate you set, an optional early warning when the
  trend and the forecast point there, and the choice to keep the alert on after it's reached.
- **Accounts and companies**: email-and-password sign-in, team invitations with admin / approver / standard roles,
  company onboarding (banks, currencies), profile and notification settings, and a tenant admin portal (`/admin`) for
  creating companies.
- Spanish and English throughout.

Coming next: conversion plans (Phase 5) and an AI assistant (Phase 6). Treasury, exposure, cash flow, billing and
self-serve sign-up are out of scope.

## Tech stack

| Part | Technology |
|---|---|
| Web app | Next.js 15 (App Router, server components and actions), TypeScript, React 19, pnpm workspace |
| UI | `@fxos/ui`, the "Violeta" component library in `packages/ui` (Manrope, CSS tokens) |
| API | .NET 10 minimal APIs, Dapper, Npgsql; Argon2id passwords, HTTP-only session cookies |
| Databases | PostgreSQL 17: a control-plane database (`fx_control`) plus one database and login per company; versioned SQL migrations in `db/` |
| Workers | Python 3.13 (`fxworkers`): Banxico FIX and interest-rate curves, forecasts and backtests, GNews headlines |
| Email | Firebase Trigger Email (Firestore `mail` collection), fed by an encrypted outbox in the control plane |
| Tests | xUnit + Testcontainers, pytest, `tsc` |

The browser only talks to the Next.js server; the Next.js server calls the API; only the workers call outside data
sources (Banxico, US Treasury, GNews), keeping an immutable raw copy before loading each company's database.

```
apps/web/          Next.js app (pages, server actions, ES/EN messages)
packages/ui/       Violeta component library
src/FxOs.Api/      REST API
src/FxOs.Platform/ Tenancy, migrations, identity, email, alerts
src/FxOs.Migrator/ CLI: migrations, platform admin, tenant maintenance
workers/python/    fxworkers: rates, curves, forecasts, news, tenant setup jobs
db/                SQL migrations (control plane and tenant)
deploy/            Dockerfiles for the API/Migrator and the workers
docs/              Operations guide, decisions, Phase 3 brief
```

## Run it locally

Prerequisites: .NET SDK 10, Node 22 with pnpm 10, Python 3.12+, Docker. A Banxico SIE token
([get one here](https://www.banxico.org.mx/SieAPIRest/service/v1/token)) for real rates; a GNews key is optional.

```bash
# 1. PostgreSQL (TimescaleDB image), Azurite (raw store) and Redis
docker compose up -d

# 2. Control-plane database (a fresh install has no companies and no dummy data)
dotnet run --project src/FxOs.Migrator -- control

# 3. Workers: install, add BANXICO_TOKEN (and optionally GNEWS_API_KEY) to workers/python/.env, load the history
cd workers/python && python3 -m venv .venv && .venv/bin/pip install -e ".[dev]"
cp .env.example .env    # then edit it
.venv/bin/fxworkers rates backfill && .venv/bin/fxworkers curves backfill
cd ../..

# 4. Web settings and dependencies
cp apps/web/.env.example apps/web/.env.local
pnpm install

# 5. Start everything: Docker, API (:5080), rate scheduler and web (:3000). Ctrl+C stops it.
./scripts/dev.sh
```

Then make yourself a platform admin and create the first company:

```bash
dotnet run --project src/FxOs.Migrator -- platform-admin you@yourcompany.com "Your Name"
```

Sign in at http://localhost:3000 with "Forgot password?" to set your password, open `/admin` and create a company. The
setup invitation goes to the company's first admin, who sets a password and adds the company's banks.

Email goes through Firebase. Locally, the API only sends to `Email:AllowedRecipients`; see
[Email through Firebase](docs/operations.md#email-through-firebase-phase-3) for the one-time setup with your Google
account. After pulling new code, apply tenant migrations with `dotnet run --project src/FxOs.Migrator -- tenants`.

Tests:

```bash
dotnet test FxOs.slnx                     # needs Docker
cd workers/python && pytest -q            # set FX_TEST_PG_DSN to also run the database tests
pnpm -r typecheck                         # also checks the ES/EN catalogs match
```

## Deploy on Railway

One Railway project with four services: **Postgres**, **api**, **workers** and **web**. Only `web` is public; the
others talk over Railway's private network.

**Before you start.** These limits come from the code as it is today:

- **Environment.** Run the API with `ASPNETCORE_ENVIRONMENT=Internal`. Other environments require Google Secret
  Manager for tenant database passwords, which the Python worker can't read yet. In `Internal`, those passwords live in
  the control-plane database.
- **Email is required.** The first admin's password and every company's setup invitation arrive by email. The API sends
  through Firebase and needs a Google credential. On Railway that is a service-account key file, and the Arkus Nexus
  Google organization currently blocks key creation. Until a key, or another sender, is available, emails wait in the
  outbox, so nobody can finish signing up.
- **No TimescaleDB.** Railway's PostgreSQL doesn't have it; the migrations skip those two statements automatically.

### 1. PostgreSQL

Add a PostgreSQL database to the project. Then create the control-plane database once, from your machine:

```bash
railway connect Postgres          # opens psql
create database fx_control;
```

The API creates one database and login per company on this same server, so it uses the server's admin user.

### 2. api

New service from this GitHub repository (root directory: the repo root).

- **Variables:**

  ```
  RAILWAY_DOCKERFILE_PATH=deploy/api.Dockerfile
  ASPNETCORE_ENVIRONMENT=Internal
  ConnectionStrings__ControlPlane=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=fx_control;Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
  TenantServers__default=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}
  App__PublicBaseUrl=https://${{web.RAILWAY_PUBLIC_DOMAIN}}/
  Email__Firebase__ProjectId=<your Firebase project id>
  Email__Firebase__CredentialsFile=<path to the service-account key file, see "Before you start">
  Email__Dispatcher__Enabled=true
  Email__AllowedRecipients__0=*@yourcompany.com
  RAILWAY_RUN_UID=0
  ```

- **Pre-deploy command:** `dotnet /app/migrator/FxOs.Migrator.dll tenants`. It migrates the control plane and every
  company database before each release.
- **Volume:** mount one at `/home/app/.aspnet/DataProtection-Keys`, so the keys that encrypt queued emails survive
  redeploys. `RAILWAY_RUN_UID=0` above lets the service write to it.
- **Networking:** no public domain. The web app reaches the API on port 8080 over the private network.

### 3. workers

New service from the same repository.

- **Variables:**

  ```
  RAILWAY_DOCKERFILE_PATH=deploy/workers.Dockerfile
  FX_CONTROL_PLANE_DSN=postgresql://${{Postgres.PGUSER}}:${{Postgres.PGPASSWORD}}@${{Postgres.PGHOST}}:${{Postgres.PGPORT}}/fx_control
  FX_TENANT_SERVER_DEFAULT=postgresql://${{Postgres.PGUSER}}:${{Postgres.PGPASSWORD}}@${{Postgres.PGHOST}}:${{Postgres.PGPORT}}/postgres
  FX_RAW_STORE_DIR=/data/raw
  BANXICO_TOKEN=<your Banxico token>
  GNEWS_API_KEY=<optional>
  RAILWAY_RUN_UID=0
  ```

- **Volume:** mount one at `/data`. That's the raw store: every Banxico, Treasury and GNews response, kept once.
- **What it runs:** the image's default command, `fxworkers rates schedule`. Every business day from 12:30 to 18:00
  Mexico City time it syncs the FIX, the interest-rate curves, forecasts and news. Every minute it also handles "Sync
  now" requests and new-company setup.
- **After the first deploy,** load the history once:

  ```bash
  railway ssh --service workers
  fxworkers rates backfill && fxworkers curves backfill
  ```

### 4. web

New service from the same repository (root directory: the repo root, because the app uses `packages/ui`).

- **Build command:** `pnpm install --frozen-lockfile && pnpm --filter @fxos/web build`
- **Start command:** `pnpm --filter @fxos/web start` (it listens on Railway's `PORT`)
- **Variables:**

  ```
  FX_API_BASE_URL=http://${{api.RAILWAY_PRIVATE_DOMAIN}}:8080
  ```

- **Node version:** use Node 22.
- **Networking:** generate a public domain, or add your own.

### 5. First admin and first company

```bash
railway ssh --service api
dotnet /app/migrator/FxOs.Migrator.dll platform-admin you@yourcompany.com "Your Name"
```

Then open the web app, use "Forgot password?" to set your password, go to `/admin` and create your company. Its
first admin gets the setup invitation by email.

A new deployment starts with no companies and no dummy data.

## More

- [docs/operations.md](docs/operations.md): how each part works (rates, forecasts, news, alerts, sign-in, email, the
  admin portal, Google Cloud hosting) and every command.
- [CLAUDE.md](CLAUDE.md): decisions, conventions and the status of each phase.
- [ADR 0001](docs/decisions/0001-database-per-tenant.md): why each company has its own database.
