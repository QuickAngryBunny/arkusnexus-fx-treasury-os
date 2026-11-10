# CLAUDE.md — FX Intelligence & Treasury OS

Read this first. It holds the decisions and conventions agreed so far; the README has run commands.

## Product
Enterprise FX and treasury platform. Built first for Arkus Nexus (USD/MXN exposure), later multi-tenant SaaS.
- PRD: project doc "FX intelligence 1.docx" (modules 01–11, phases 1–6).
- Build plan (phases 0–6, sprints, exit criteria): https://claude.ai/artifact/UvdTvey4XkCdchDU65Mat4
- Design canvas / prototype (screen reference, Violeta components page): https://claude.ai/code/artifact/f8510a18-6085-4aa2-bb6b-6226f7065c56
- One developer, one-week sprints.

## Rollout rules (do not break)
- Phases 0–2: internal environment only, dummy tenants and dummy company data. Only public exchange rates are real.
- Phase 3 adds sign-up, sign-in and MFA, then Arkus Nexus loads real data. Nothing real before that passes.
- Phase 6: billing/subscriptions and other companies. Two roles only: `standard` and `admin`; only admins manage billing.
- Rate alerts (threshold email) ship in Phase 2; AI-driven alerts in Phase 5. LLM: Azure OpenAI, GPT‑6.1 Terra (Phase 5).

## Architecture (ADR 0001: docs/decisions/0001-database-per-tenant.md)
- One PostgreSQL + TimescaleDB database and login role per tenant, including its own copy of rate history.
  Control plane (`fx_control`) holds only tenants, routing, schema version (identities/subscriptions later).
- Web (Next.js) → .NET 10 API only; the browser never calls the API directly (server components/actions).
- User-facing queries never call external data APIs; only ingestion workers do (Phase 1, raw store in Blob, per-tenant fan-out).
- Financial calculations are deterministic services; the LLM never computes money values.

## Conventions
- Tenant-scoped endpoints live under `/api/tenant/*` and get their DB via `ITenantConnectionFactory` only.
  `TenantIsolationTests.Every_tenant_route_requires_a_tenant` checks every route automatically.
- Write changes and their `IAuditWriter` entry in the same transaction. `audit_log` is append-only.
- Migrations: add `db/tenant/V000N__name.sql` or `db/control/V000N__name.sql`, numbered without gaps.
  Never edit an applied migration (checksums fail the run). Keep SQL LF line endings.
- Money: always keep original currency and amount; a converted value stores the applied rate, source and time.
- Every new tenant feature gets a cross-tenant test. Dummy data only via `workers/python` (refuses non-dummy tenants).
- Spanish and English from the first screen; currency always shown with its code.
- Display currency values (amounts, exchange rates, errors in currency) with 2 decimals via `formatMoney`/`formatRate`
  (`CURRENCY_DIGITS` in `apps/web/lib/i18n.ts`); storage and calculations keep full precision.

## UI decisions
- "Violeta" direction: violet/plum, rounded cards, based on the reference mobile app; font **Manrope** (not Urbanist).
- Inner pages: breadcrumb above the title, no back buttons.
- Main menu section "Forecast Intelligence" combines rate history, forecast and market intelligence: current rate,
  trend and volatility up front; forecast ahead; past rates with the reason they moved; full history by year/month/day
  with gap/duplicate fixes and manual source sync.

## Status
- Phase 0 sprints 1–2 done. Sprint 2: `@fxos/ui` (Violeta tokens + Button, Badge, Field/SelectField, Switch,
  Segmented, StatCard, AlertBanner, PageHeader, Breadcrumb, AppNav, UserMenu), app shell (`components/AppShell.tsx`),
  ES/EN catalogs in `apps/web/messages/`, `/ui` component reference page, placeholder pages for the other sections.
- UI conventions: build screens from `@fxos/ui`; add tokens/components there, not in app CSS. Every user-facing string
  goes in both catalogs (typecheck fails if keys differ). Pass `linkAs={Link}` to link-rendering components.
- Phase 1 (partial): tenant migration V0002 (rate_source, fx_rate hypertable, rate_load);
  `fxworkers rates sync|backfill|load|schedule` (Banxico SF43718 → raw store in Blob/Azurite → every tenant,
  idempotent, revisions audited; schedule = business days 12:30–18:00 Mexico City, started by dev.sh);
  `/api/tenant/rates/*`. Banxico token: `workers/python/.env` only (`BANXICO_TOKEN`), never committed or logged.
  Still to do: DOF connector, per-tenant source/recurrence settings, gap/duplicate detection and fixes, versioned
  manual corrections, R-02 full history (year/month/day, chart), export drawer, source-down state.
- Phase 2 (started): V0003 treasury foundation (bank_account, financial_transaction with conversion legs) seeded with
  dummy data by `fxworkers seed`; V0004 forecasting (model registry, runs, points with 80/95% intervals, backtests);
  V0005 interest-rate curves (TIIE + US Treasury, `fxworkers curves sync|backfill`) and model display order; models
  `forward` (interest-rate parity), `ewma_vol`, `naive`; `fxworkers forecast run`; APIs for bank accounts, transactions, monthly cash flow, analytics, forecasts; dashboard
  cash by currency + accounts; Forecast Intelligence with analytics, forecast table and track record vs naive.
  Treasury screens and real bank feeds stay in Phase 3. Next in Phase 2: rate alerts with email (P2-04, P2-S2),
  economic indicators/events and "why it moved", driver-based models, R-01 chart with horizon filter and event
  markers, the dashboard convert-or-wait signal.
- Forecast rule: publish every model's backtest next to the naive benchmark; do not present a model as better than
  naive unless `mae_vs_naive` < 1 for that horizon.
- Phase 0 sprint 3 (still open): Azure internal environment, deploy workflow running per-tenant migrations, OpenTelemetry (tenant id
  tag), Grafana, split migration-owner vs app role.
- Housekeeping: ~50 analyzer warnings to clear before `TreatWarningsAsErrors`.
