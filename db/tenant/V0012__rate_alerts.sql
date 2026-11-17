-- Rate alerts and trend early warnings (Phase 4, P2-04). An alert belongs to the person who created it and only ever
-- emails them. After each FIX load and each forecast run the API's alert checker moves an alert through its states:
--   reached       the FIX is at or past the rate: one email. keep_on off → the alert switches off (is_on = false);
--                 keep_on on → armed = false until the FIX moves back across the rate, so one email per crossing.
--   early warning the FIX moved toward the rate on most recent days and the stored forecast gives a better than 50%
--                 chance of reaching it within look_ahead_days: one email, then warned = true until either condition
--                 breaks.
-- The checker is replay safe: checked_fix_date / checked_run_id record what it has seen, and an event is unique per
-- alert, kind and FIX date.

create table rate_alert (
    id                bigint generated always as identity primary key,
    user_id           uuid not null,  -- control-plane user_account.id
    source_code       text not null default 'banxico_fix' references rate_source (code),
    direction         text not null check (direction in ('above', 'below')),
    rate              numeric(18, 6) not null check (rate > 0 and rate < 1000),
    early_warning     boolean not null default true,
    look_ahead_days   int not null default 7 check (look_ahead_days in (7, 15, 30)),
    keep_on           boolean not null default false,
    is_on             boolean not null default true,
    armed             boolean not null default true,
    warned            boolean not null default false,
    checked_fix_date  date,
    checked_run_id    bigint references forecast_run (id),
    reached_at        timestamptz,
    created_at        timestamptz not null default now(),
    updated_at        timestamptz not null default now(),
    deleted_at        timestamptz
);

create index rate_alert_user_idx on rate_alert (user_id, created_at) where deleted_at is null;
create index rate_alert_active_idx on rate_alert (source_code) where deleted_at is null and is_on;

-- Every check that sends (or would send) an email. id is also the email's id in the control-plane outbox, so a copy
-- retried after a crash is never sent twice. email_status: pending (not yet in the outbox), queued, or skipped (the
-- person turned that email off in notification settings, or is no longer an active member).
create table rate_alert_event (
    id               uuid primary key default gen_random_uuid(),
    alert_id         bigint not null references rate_alert (id),
    kind             text not null check (kind in ('reached', 'early_warning')),
    fix_date         date not null,
    fix_rate         numeric(18, 6) not null check (fix_rate > 0),
    forecast_run_id  bigint references forecast_run (id),
    chance           numeric(5, 4) check (chance between 0 and 1),
    detail           jsonb not null default '{}'::jsonb,
    email_status     text not null default 'pending' check (email_status in ('pending', 'queued', 'skipped')),
    created_at       timestamptz not null default now(),
    unique (alert_id, kind, fix_date)
);

create index rate_alert_event_pending_idx on rate_alert_event (created_at) where email_status = 'pending';

-- U-03: "Rate target reached" now means these alerts; early warnings get their own switch.
insert into notification_type (code, default_enabled, approvers_only, sort_order) values
    ('rate_early_warning', true, false, 10);
