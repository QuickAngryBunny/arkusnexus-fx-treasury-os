-- Phase 3: identities, memberships, invitations, account tokens, sessions, email outbox and the control-plane audit log.
-- Only what the app needs before it knows which tenant a request belongs to. Profiles, preferences, company info and
-- banks live in each tenant database (tenant V0009).
-- Tokens: store only SHA-256 hashes (32 bytes) of the random value sent by email or set as a cookie, never the raw token.

-- Tenants can be archived (A-04). Real-data tenants can only be archived, never deleted. Archived tenants are not
-- active, so tenant resolution refuses them.
alter table tenant drop constraint tenant_status_check;
alter table tenant add constraint tenant_status_check
    check (status in ('provisioning', 'active', 'suspended', 'failed', 'archived'));
alter table tenant add column archived_at timestamptz;
alter table tenant add constraint tenant_archived_at_matches_status
    check ((status = 'archived') = (archived_at is not null));

-- One login identity per person, shared across the tenants they belong to.
create table user_account (
    id                    uuid primary key default gen_random_uuid(),
    -- Stored trimmed and lower-cased so uniqueness and sign-in lookups are case-insensitive.
    email                 text not null unique
                          check (email = lower(btrim(email)) and email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' and length(email) <= 254),
    display_name          text not null check (length(btrim(display_name)) between 1 and 200),
    -- PHC string ($argon2id$...). Null until the person sets a password through an invitation.
    password_hash         text check (password_hash is null or password_hash like '$argon2id$%'),
    password_changed_at   timestamptz,
    -- Internal staff who use the tenant admin portal (A-01..A-06). Never set through the FX app.
    is_platform_admin     boolean not null default false,
    -- Lock after repeated failed sign-ins (T-S2): locked_until = now() + 15 minutes.
    failed_sign_in_count  int not null default 0 check (failed_sign_in_count >= 0),
    locked_until          timestamptz,
    last_sign_in_at       timestamptz,
    created_at            timestamptz not null default now(),
    updated_at            timestamptz not null default now(),
    check ((password_hash is null) = (password_changed_at is null))
);

-- A person's role in one tenant. Deactivated members can't sign in to that tenant; their history stays.
create table tenant_membership (
    tenant_id       uuid not null references tenant (id),
    user_id         uuid not null references user_account (id),
    role            text not null check (role in ('admin', 'approver', 'standard')),
    status          text not null default 'active' check (status in ('active', 'deactivated')),
    deactivated_at  timestamptz,
    deactivated_by  uuid references user_account (id),
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    primary key (tenant_id, user_id),
    check ((status = 'deactivated') = (deactivated_at is not null))
);

create index tenant_membership_user_idx on tenant_membership (user_id);

-- Setup invitation for a tenant's first admin (O-01) or teammate invitation (T-04).
-- Single use, 7-day expiry; sending a new link revokes the open one.
create table invitation (
    id            uuid primary key default gen_random_uuid(),
    tenant_id     uuid not null references tenant (id),
    kind          text not null check (kind in ('tenant_setup', 'teammate')),
    email         text not null check (email = lower(btrim(email))),
    display_name  text not null check (length(btrim(display_name)) between 1 and 200),
    role          text not null check (role in ('admin', 'approver', 'standard')),
    token_hash    bytea not null unique check (length(token_hash) = 32),
    -- Null for setup invitations sent by the tenant admin portal on tenant creation.
    invited_by    uuid references user_account (id),
    expires_at    timestamptz not null,
    used_at       timestamptz,
    used_by       uuid references user_account (id),
    revoked_at    timestamptz,
    created_at    timestamptz not null default now(),
    check (kind <> 'tenant_setup' or role = 'admin'),
    check (expires_at > created_at),
    check (used_at is null or revoked_at is null),
    check ((used_at is null) = (used_by is null))
);

-- At most one open invitation per tenant and email.
create unique index invitation_open_uq on invitation (tenant_id, email) where used_at is null and revoked_at is null;
create index invitation_tenant_idx on invitation (tenant_id, created_at desc);

-- Password reset (T-09, 1-hour expiry) and email-change confirmation (U-01). Single use; a new one revokes the open one.
create table account_token (
    id          uuid primary key default gen_random_uuid(),
    user_id     uuid not null references user_account (id),
    purpose     text not null check (purpose in ('password_reset', 'email_change')),
    token_hash  bytea not null unique check (length(token_hash) = 32),
    -- The address being confirmed; only for email_change.
    new_email   text check (new_email is null or new_email = lower(btrim(new_email))),
    expires_at  timestamptz not null,
    used_at     timestamptz,
    revoked_at  timestamptz,
    created_at  timestamptz not null default now(),
    check ((purpose = 'email_change') = (new_email is not null)),
    check (expires_at > created_at),
    check (used_at is null or revoked_at is null)
);

create unique index account_token_open_uq on account_token (user_id, purpose) where used_at is null and revoked_at is null;

-- Signed-in browser sessions. The cookie holds the raw token; only its hash is stored.
create table session (
    id            uuid primary key default gen_random_uuid(),
    user_id       uuid not null references user_account (id),
    -- The tenant the session is working in; sign-in lands on the user's own company.
    tenant_id     uuid references tenant (id),
    token_hash    bytea not null unique check (length(token_hash) = 32),
    created_at    timestamptz not null default now(),
    last_seen_at  timestamptz not null default now(),
    expires_at    timestamptz not null,
    revoked_at    timestamptz,
    -- Why it ended: sign_out, password_changed, deactivated, expired...
    revoked_reason text,
    user_agent    text check (length(user_agent) <= 512),
    ip_address    inet,
    check (expires_at > created_at),
    check ((revoked_at is null) = (revoked_reason is null))
);

create index session_user_active_idx on session (user_id) where revoked_at is null;

-- Transactional outbox for email. Written in the same transaction as the change that triggers the email; a dispatcher
-- copies pending rows to the Firebase Trigger Email `mail` collection. Firebase holds only the email being sent.
create table email_outbox (
    id               uuid primary key default gen_random_uuid(),
    tenant_id        uuid references tenant (id),
    -- Template key, e.g. tenant_setup_invitation, teammate_invitation, welcome, member_joined, password_reset,
    -- password_changed, email_change_confirmation, role_changed, account_deactivated.
    template         text not null check (template ~ '^[a-z][a-z0-9_]{2,63}$'),
    locale           text not null check (locale in ('en-US', 'es-MX')),
    to_email         text not null check (to_email = lower(btrim(to_email))),
    subject          text not null check (length(subject) between 1 and 300),
    html             text not null,
    text_body        text,
    status           text not null default 'pending' check (status in ('pending', 'sending', 'sent', 'failed')),
    attempts         int not null default 0 check (attempts >= 0),
    next_attempt_at  timestamptz not null default now(),
    last_error       text,
    -- Firestore document id in the `mail` collection; equal to id so a retried write is idempotent.
    provider_ref     text,
    created_at       timestamptz not null default now(),
    sent_at          timestamptz,
    check ((status = 'sent') = (sent_at is not null))
);

create index email_outbox_due_idx on email_outbox (next_attempt_at) where status in ('pending', 'sending');

-- Append-only audit trail for control-plane changes (sign-ins, lockouts, invitations, role changes, tenant admin).
-- Tenant-scoped changes also get an entry in that tenant's audit_log.
create table control_audit_log (
    id              bigint generated always as identity primary key,
    occurred_at     timestamptz not null default clock_timestamp(),
    actor_user_id   uuid references user_account (id),
    -- 'user', 'platform_admin', 'system' or 'anonymous' (e.g. a failed sign-in for an unknown email).
    actor_kind      text not null check (actor_kind in ('user', 'platform_admin', 'system', 'anonymous')),
    tenant_id       uuid references tenant (id),
    action          text not null check (action ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'),
    entity_type     text,
    entity_id       text,
    ip_address      inet,
    detail          jsonb not null default '{}'::jsonb
);

create index control_audit_log_tenant_idx on control_audit_log (tenant_id, occurred_at desc);
create index control_audit_log_actor_idx on control_audit_log (actor_user_id, occurred_at desc);

create function control_audit_log_reject_change() returns trigger
language plpgsql as $$
begin
    raise exception 'control_audit_log is append-only (% blocked)', tg_op;
end;
$$;

create trigger control_audit_log_no_update_delete
    before update or delete on control_audit_log
    for each row execute function control_audit_log_reject_change();

create trigger control_audit_log_no_truncate
    before truncate on control_audit_log
    for each statement execute function control_audit_log_reject_change();
