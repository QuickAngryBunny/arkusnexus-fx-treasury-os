# CLAUDE.md — FX Intelligence & Treasury OS

Read this first. It holds the decisions and conventions agreed so far; the README has run commands.

## Product
USD/MXN exchange-rate platform. Built first for Arkus Nexus, later multi-tenant SaaS.
Scope (narrowed, see `docs/phase-3-brief.md`): rate history and forecast + news (Phases 0–2), accounts/team/tenant admin
(Phase 3), conversion plans as the main action (Phase 4), AI assistant (Phase 5). **Out of scope — don't build or
scaffold:** treasury, currency exposure, cash flow, simulator, profitability, billing/subscriptions, self-serve sign-up,
MFA/two-step verification.
- PRD: project doc "FX intelligence 1.docx" (modules 01–11; narrowed by the Phase 3 brief).
- Build plan (phases 0–6, sprints, exit criteria): https://claude.ai/artifact/UvdTvey4XkCdchDU65Mat4
- Design canvas with user journeys + clickable prototype (screen codes O-*, T-*, A-*, U-*, C-*): https://claude.ai/artifact/XfTxqdpgPriSXYT9Du7MHw
- Earlier design canvas (R-*, P*-* artboards, Violeta components page): https://claude.ai/code/artifact/f8510a18-6085-4aa2-bb6b-6226f7065c56
- Architecture whiteboard: https://claude.ai/artifact/GSGKEGsD7SZZXirjqLFeTw
- One developer, one-week sprints.

## Rollout rules (do not break)
- Phases 0–2: internal environment only, dummy tenants and dummy company data. Only public exchange rates are real.
- Development runs locally until the last phase; the DevOps developer then deploys to Azure. Don't build Azure infra
  (private network, Key Vault provisioning, backups, deploy workflows); keep seams like `ISecretStore` for them.
- Phase 3 gate (private network only, backups, encryption, Key Vault, restore drill, security review) is part of that
  hand-off. Sign-in is the app's own email + password (Argon2id/bcrypt hash in the control plane, HTTP-only session
  cookies, 15-minute lock). No MFA, no outside identity provider. Tenants are created by internal tenant admins (no self-serve sign-up).
  Arkus Nexus loads real data only after the gate passes.
- Roles per tenant membership: `admin`, `approver`, `standard`. Only admins see Team, invite, change roles, edit banks.
- Email: Firebase is only the sender (Trigger Email extension, Firestore `mail` collection). Firebase does no auth and
  holds no user data. Locally the API writes to the real Firebase project (test addresses only); the service-account
  key stays outside the repo (Key Vault after the hand-off).
- Alerts belong to conversion plans (Phase 4); AI in Phase 5. LLM: Azure OpenAI, GPT‑6.1 Terra (Phase 5).

## Architecture (ADR 0001: docs/decisions/0001-database-per-tenant.md)
- One PostgreSQL + TimescaleDB database and login role per tenant, including its own copy of rate history.
  Control plane (`fx_control`) holds only tenants, routing, schema version, and (Phase 3) identities, memberships,
  invitations, password resets and sessions. Store only token hashes, never raw tokens.
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
- Main menu section "Exchange Forecast" (ES "Pronóstico cambiario"; formerly Forecast Intelligence) combines rate history, forecast and market intelligence: current rate,
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
  Still to do: DOF connector, per-tenant source/recurrence settings, versioned manual value corrections, export
  drawer (P2-06), source-down state (P2-S1).
- Phase 2: V0004 forecasting (model registry, runs, points with 80/95% intervals, backtests);
  V0005 interest-rate curves (TIIE + US Treasury, `fxworkers curves sync|backfill`) and model display order; models
  `forward` (interest-rate parity), `ewma_vol`, `naive`; `fxworkers forecast run`; APIs for analytics and forecasts; Exchange Forecast follows R-01 (today hero card, trend, 30D volatility level, plum
  forecast card + 12-month chart with 7D–180D horizon pills, biggest one-day move, rate-gap driver) and
  `/forecast/models` follows P4-04 (leaderboard vs naive per horizon). Build screens from the design canvas artboards
  (R-*, P*-*, J*), not from scratch; "Alert me" (P2-04) stays disabled until built. `/forecast/history` follows R-02:
  year/month/day views (URL-driven), gaps from a deterministic Mexican bank-holiday calendar (`MexicanBankCalendar`),
  "Mark holiday" (V0006 `rate_gap_resolution`, audited), "Re-fetch"/"Sync now" record a `sync_request` that the
  worker's scheduler processes every minute (fetch once, load every tenant), CSV export of the shown view.
  Dashboard: greeting + today's rate card (reuses the Exchange Forecast data); O-05 adds the rest in Phase 3.
  News (V0007 `rate_move`, `news_window`, `news_article`): `fxworkers news sync` flags moves beyond ±2σ of the previous
  30 days, fetches GNews once per window into the raw store and copies into every tenant; `GET /api/tenant/news` reads
  the tenant DB only. R-01 shows move markers, a Today button and the News card. `GNEWS_API_KEY` in
  `workers/python/.env` only; free plan = last 30 days, 12 h delay, 100 req/day (set `GNEWS_PLAN=paid` to widen).
  Still open from Phase 2: economic indicators/events and "why it moved", driver-based models, R-01 chart with
  horizon filter and event markers.
- Phase 3 (started, brief: `docs/phase-3-brief.md`): V0008 dropped the treasury scaffolding (bank_account,
  financial_transaction, legal_entity, department; dashboard cash/exposure/projection; /treasury, /exposure,
  /simulator). `fxworkers seed` now writes dummy users only. Dev tenant switcher stays until sign-in (Sprint 2).
  Control V0002: `user_account` (lower-case email, Argon2id PHC hash, lock state, `is_platform_admin` for the tenant
  admin portal), `tenant_membership` (admin/approver/standard, active/deactivated), `invitation` (tenant_setup or
  teammate, one open per tenant+email), `account_token` (password_reset / email_change, one open per user+purpose),
  `session`, `email_outbox` (pending → Firebase `mail`), append-only `control_audit_log`; tenant status `archived`
  (+ `archived_at`). Token columns are 32-byte SHA-256 hashes.
  Tenant V0009: `user_profile` rebuilt (`user_id` = control `user_account.id`; language, number format, email time
  zone; no role — roles live only in `tenant_membership`), `notification_type` (9 U-03 items with defaults; weekly
  summary off) + `notification_preference` (rows only when changed), company info as new `tenant_settings` columns
  (legal name, country, conversion direction, reporting currency, `onboarding_completed_at`), `bank` (name + MXN/USD,
  soft-removed, unique per currency ignoring case). Triggers: onboarding can't complete without one MXN and one USD
  bank; afterwards the last bank of a currency can't be removed.
  Passwords (`FxOs.Platform/Identity`): `IPasswordHasher` = Argon2id (Konscious) as PHC strings, OWASP parameters
  m=19456 KiB, t=2, p=1, 16-byte salt; `Verify` returns `SucceededRehashNeeded` for older parameters (rehash on sign-in);
  call `VerifyAgainstDummy` for unknown emails so timing doesn't reveal accounts. Input is NFC-normalised.
  `PasswordPolicy.Check` returns rule codes `min_length` (12), `max_length` (128), `number`, `symbol`; lengths count
  visible characters. The web forms must mirror these codes for the live checks.
  `SecureToken` (32 random bytes, 43-char base64url raw; SHA-256 stored) and `IdentityStore` (control plane, uses
  `TimeProvider`): invitations 7 days (resend revokes the open one; refused if the email is already an active member;
  accept creates or links the account — an existing password is kept — and adds/reactivates the membership; refused for
  non-active tenants), password reset 1 hour (clears the lock, ends all sessions), email change 24 hours (refused if the
  address is taken), 5 failed sign-ins → 15-minute lock. Links are single use (row lock on claim). Every change writes
  `control_audit_log` (`ControlAudit`) in the same transaction; raw tokens never reach the database or the audit log.
- Forecast rule: publish every model's backtest next to the naive benchmark; do not present a model as better than
  naive unless `mae_vs_naive` < 1 for that horizon.
- Phase 0 sprint 3 (moved to the DevOps hand-off): Azure internal environment, deploy workflow running per-tenant
  migrations, OpenTelemetry (tenant id tag), Grafana. Split migration-owner vs app role is still ours, before real data.
- Housekeeping: ~50 analyzer warnings to clear before `TreatWarningsAsErrors`.
