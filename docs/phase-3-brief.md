# Phase 3 brief — Accounts, team and tenant admin

Hand-off for Claude Code. Save this file in the repo as `docs/phase-3-brief.md`, then paste the prompt at the bottom into Claude Code.

## Where this comes from

- Build plan (Claude doc): https://claude.ai/artifact/UvdTvey4XkCdchDU65Mat4
- Architecture whiteboard: https://claude.ai/artifact/GSGKEGsD7SZZXirjqLFeTw
- Design canvas with the user journeys and clickable prototype: https://claude.ai/artifact/XfTxqdpgPriSXYT9Du7MHw

These links are private claude.ai pages. Claude Code can't open them, so everything it needs is written out below. Screen codes (O-02, T-05…) match the prototype boards, so you can check each screen against the design yourself.

## Scope after the latest changes

The product is now: exchange-rate history and forecast, already in the app (Phases 0 to 2); **conversion plans as the main action** (Phase 4); and an AI assistant (Phase 5). Treasury, currency exposure, cash flow, the simulator, profitability, billing, self-serve sign-up and two-step verification are **out of scope**. Don't build or scaffold them.

| Phase | Content |
|---|---|
| 0 Foundations | Under way: monorepo, per-tenant databases, tenant menu, design system |
| 1 Exchange-rate history | Under way: Banxico and DOF, backfill, gaps, corrections |
| 2 Forecast and news | Forecast models, ranges, backtests, GNews per date, dashboard rate and news cards |
| **3 Accounts, team and tenant admin** | **This brief** |
| 4 Conversion plans | Plans, options from the forecast, approval, alerts, rate log, results, dashboard tracking |
| 5 AI assistant | Questions about the forecast and plans |

## Architecture rules that still apply

- Next.js + React + TypeScript web app; .NET 10 modular monolith API (REST); Python workers.
- One PostgreSQL + TimescaleDB database **per tenant**. A small control-plane database holds tenants, users, memberships, invitations and routing.
- The API resolves the caller's tenant on every request and opens only that tenant's database.
- Append-only audit log in each tenant database.
- Spanish and English from the first screen.
- Violeta design system with the Manrope font. Inner pages put a breadcrumb above the title.

## Phase 3 goals

Arkus Nexus and its team are signed in on **real data**, with their banks and approvers in place, before conversion plans are built.

### 1. Protect real data first (gate)

- The app and tenant admin are reachable only from the Arkus Nexus private network (VPN or private endpoint).
- Backups and encryption at rest and in transit are on. Secrets live in Azure Key Vault.
- The audit log records every change.
- Pass a restore drill and a security review of the environment.
- Real data is loaded only after this gate passes.

### 2. Sign-in (the app's own; no outside identity provider)

- Email and password, stored as an Argon2id or bcrypt hash in the control plane.
- Sessions in secure HTTP-only cookies.
- After repeated failed attempts, lock the account for 15 minutes (screen T-S2).
- After sign-in, the user lands on the dashboard of their own company only.
- Password rules: at least 12 characters, with a number and a symbol. The forms check them live.

### 3. Email through Firebase (sending only)

- Firebase is used **only as the email sender**: the Trigger Email extension, sending from the email account already registered in Firebase.
- The API (or a .NET worker) writes a document to the extension's Firestore `mail` collection with to, subject and HTML.
- Firebase does no authentication, and holds no user data beyond the email being sent.
- Keep the Firebase service-account credentials and the account's SMTP password in Key Vault, never in the repo.
- Emails in this phase:
  - Tenant setup invitation (O-01).
  - Teammate invitation (T-04).
  - Welcome email.
  - "X joined", sent to the admin.
  - Password reset link (T-09).
  - "Your password was changed".
  - Confirmation link when a user changes their email.
  - Role changed, and account deactivated.

### 4. Tenant admin (journey J6, screens A-01 to A-06, A-S1 to A-S3)

- A separate portal, internal only.
- **A-01 Tenants:** a table of every tenant with its data type (Real or Dummy) and status. A-S1 is the empty state.
- **A-02 New tenant:** a drawer with the tenant name and the first admin's email.
- **A-03 Setup:**
  - Creating a tenant creates its database, runs migrations, registers the routing and copies the rate history from the raw store.
  - The screen shows the steps as they run; a failed step offers Retry (A-S2), and setup can be resumed.
  - When setup finishes, the first admin is emailed a setup invitation.
- **A-04 Tenant page:** load or clear dummy data, rename, archive, or delete with type-to-confirm (A-S3). Real-data tenants can only be archived.
- **Tenant menu:** in the FX app it sits above the user card at the bottom of the sidebar (A-05). On mobile it sits inside the user menu (A-06).

### 5. Onboarding of the first admin (journey J1, screens O-01 to O-05, O-S1, O-S2)

- **Setup link:** single use, expires in 7 days. Sending a new link replaces the old one. An expired or used link shows O-S1.
- **Step 1 of 3 (O-02):** name, prefilled from the invitation; the email shown but locked; password with its rules checked live.
- **Step 2 of 3 (O-03):** company name, legal name (optional), country, time zone, which way they convert, and language.
- **Step 3 of 3 (O-04):** banks typed in as tags, one input for pesos (MXN) and one for dollars (USD), with suggestions.
  - At least one bank of each currency is required (O-S2).
  - No account numbers and no bank connection.
  - Duplicate names within a currency are ignored.
- **First-visit dashboard (O-05):**
  - "Welcome, Ana", today's rate card and the "News moving the peso" card.
  - A "Create your first conversion plan" card showing the bank tags.
  - An empty "Ongoing conversions" card and an "Invite your team" link.
  - The plan button can point to a placeholder until Phase 4.
  - Leave out or disable the floating Ask FX AI until Phase 5.
- If the admin leaves before adding banks, setup resumes at step 3.

### 6. Team (journeys J4 and J5)

**Admin side, in Settings › Team** (T-01 to T-03, T-S3):

- **Users table:** name, email, role, status (active, invited, deactivated) and last sign-in, with filters.
- **Row menu:** change role, resend invitation, cancel invitation, deactivate.
- **Invite drawer (T-02):** name, email, and the role, Standard or Approver.
  - T-03 confirms the invitation was sent.
  - T-S3 shows when the email is already in the company.
- **Roles:** every membership is admin, approver or standard.
  - Only admins see Team, invite people and change roles.
  - Approvers become pickable on conversion plans in Phase 4.
- **Deactivated users:** they can't sign in, and their history stays.

**Invited person's side** (T-04 to T-11, T-S1, T-S2):

- **Invitation (T-04 → T-05):** the email reads "Ana Ruiz invited you to Arkus Nexus on FX Intelligence" with a "Join Arkus Nexus" button.
  - The link is single use and expires in 7 days; an expired link shows T-S1.
  - The join page (T-05) shows the name and email filled in, then password and confirm. Joining signs them in.
- **Sign in (T-06).**
- **Forgot password (T-07 → T-08):** the page always answers "If that email is registered, we sent a link", whether or not the account exists.
- **Reset (T-09 → T-10 → T-11):** the reset link is single use and expires in 1 hour. Changing the password signs out other sessions and sends a confirmation email.

### 7. User settings (journey J4, screens U-01 to U-03, C-01)

**Settings menu:** Your account (Profile, Security, Notifications) and Company (Company info, Team). Team is admins only. There is no Subscription item.

- **Profile (U-01):**
  - Full name and work email; changing the email sends a confirmation link first.
  - Preferences: language (English or Español), number format (1,234.56 or 1.234,56), and the time zone used for email times.
  - No photo, phone or job title.
- **Security (U-02):** change the password with the current one, the new one and a confirmation. Nothing else on this page.
- **Notifications (U-03):** an email on/off switch for each item below. Store the preferences now; Phase 4 and later send them.
  - Conversion reminders.
  - Final rate needed.
  - Plan status changes.
  - Rate reaches a target.
  - Approval requests (approvers only).
  - Approval decisions.
  - Plans you follow.
  - Forecast moves.
  - Weekly forecast summary (off by default).
- **Company info (C-01, admins edit):**
  - Company name, base currency and reporting currency.
  - Banks shown as MXN and USD tags: click a name to rename it, × to remove it, or add new ones.
  - Renaming a bank updates every reference to it.
  - At least one bank of each currency must remain.
  - From Phase 4, a bank used by an ongoing plan can't be removed.

## Data to add

| Where | Entities |
|---|---|
| Control plane | Tenant (with its status and data type), user login identity (email, password hash, lock state), tenant membership with role, invitation (token hash, expiry, invited by, used at), password reset (token hash, expiry), session, database routing and schema version |
| Tenant DB | User profile and preferences, notification preferences, company info, Bank (name, currency MXN or USD), audit log entries for sign-ins, invitations, role changes, bank and setting changes |

Store only token hashes, never raw tokens.

## Exit criteria

- The app and tenant admin can't be reached from outside the private network.
- A test admin creates a working tenant with a name and one email; the first admin finishes onboarding with at least one peso bank and one dollar bank.
- Every user signs in with their own password and sees only their company.
- Only tenant admins can invite users, change roles or edit banks, and endpoint tests prove it.
- Invitation and reset links work once and expire.
- Every email above is sent through the Firebase Trigger Email extension.
- A restore drill succeeds before real data loads.

## Suggested one-week sprints

1. Real-data gate (private network, backups, Key Vault) plus the identity tables and migrations.
2. Sign-in, sessions, account lock, forgot and reset password, the Firebase email sender, email templates in English and Spanish.
3. Tenant admin portal with setup progress, plus the tenant menu.
4. Onboarding steps 1 to 3 plus the first-visit dashboard.
5. Team: invite, join, roles, row actions.
6. Settings: profile, password, notifications, company info and bank tags.
7. Isolation and role tests, security review, restore drill, then load Arkus Nexus's real data.

---

## Prompt to paste into Claude Code

```
Read docs/phase-3-brief.md. It is the agreed scope for Phase 3 (accounts, team and tenant admin) after we narrowed the product to exchange rates + conversion plans.

First, explore the repo and tell me:
1. What already exists from Phases 0–2 that Phase 3 builds on (tenant provisioning, tenant menu, design system components, auth stubs, email code).
2. Anything in the repo that conflicts with the brief (e.g. scaffolding for treasury, billing, MFA or an outside auth provider) and what you'd remove.
3. A plan for Sprint 1 only, as small reviewable steps with the tests for each.

Don't write code until I approve the Sprint 1 plan. Rules: the app keeps its own email-and-password sign-in; Firebase is only the email sender, through the Trigger Email extension; no MFA, no billing, no treasury.
```
