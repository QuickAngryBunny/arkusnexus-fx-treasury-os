-- Phase 3: user profiles with preferences, notification preferences, company info and banks.
-- Login identity, role and membership status live in the control plane (user_account, tenant_membership).
-- The tenant keeps what the person sees and sets inside the company. Every tenant holds dummy data only at this point.

-- user_profile is rebuilt: its id is now the control-plane user_account id, and role moves to tenant_membership.
drop table user_profile;

create table user_profile (
    -- Same value as fx_control.user_account.id (different database, so no foreign key).
    user_id        uuid primary key,
    -- Copy of the sign-in email for display; updated when an email change is confirmed.
    email          text not null unique check (email = lower(btrim(email))),
    display_name   text not null check (length(btrim(display_name)) between 1 and 200),
    -- U-01 preferences.
    language       text not null default 'es-MX' check (language in ('es-MX', 'en-US')),
    number_format  text not null default 'period_decimal' check (number_format in ('period_decimal', 'comma_decimal')),
    -- Time zone used for times in emails. Validated against the IANA list by the API.
    time_zone      text not null default 'America/Mexico_City' check (length(time_zone) between 1 and 64),
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

comment on column user_profile.number_format is 'period_decimal = 1,234.56; comma_decimal = 1.234,56';

-- U-03: one email switch per notification. Defaults live here; preference rows exist only once a user changes one.
-- Phase 4 and later send these.
create table notification_type (
    code             text primary key check (code ~ '^[a-z][a-z0-9_]{2,63}$'),
    default_enabled  boolean not null,
    approvers_only   boolean not null default false,
    sort_order       smallint not null unique
);

insert into notification_type (code, default_enabled, approvers_only, sort_order) values
    ('conversion_reminder',     true,  false, 1),
    ('final_rate_needed',       true,  false, 2),
    ('plan_status_change',      true,  false, 3),
    ('rate_target_reached',     true,  false, 4),
    ('approval_request',        true,  true,  5),
    ('approval_decision',       true,  false, 6),
    ('followed_plan',           true,  false, 7),
    ('forecast_move',           true,  false, 8),
    ('weekly_forecast_summary', false, false, 9);

create table notification_preference (
    user_id            uuid not null references user_profile (user_id) on delete cascade,
    notification_code  text not null references notification_type (code),
    email_enabled      boolean not null,
    updated_at         timestamptz not null default now(),
    primary key (user_id, notification_code)
);

-- Company info (O-03, C-01) extends the single tenant_settings row. display_name is the company name,
-- locale the company language.
alter table tenant_settings
    add column legal_name               text check (legal_name is null or length(btrim(legal_name)) between 1 and 200),
    add column country                  char(2) not null default 'MX' check (country ~ '^[A-Z]{2}$'),
    add column conversion_direction     text not null default 'usd_to_mxn'
                                        check (conversion_direction in ('usd_to_mxn', 'mxn_to_usd', 'both')),
    add column reporting_currency       char(3) not null default 'MXN',
    -- Set when the first admin finishes step 3 (O-04). Until then setup resumes where it stopped.
    add column onboarding_completed_at  timestamptz;

alter table tenant_settings
    add constraint tenant_settings_base_currency_check check (base_currency in ('MXN', 'USD')),
    add constraint tenant_settings_reporting_currency_check check (reporting_currency in ('MXN', 'USD')),
    add constraint tenant_settings_base_currency_fk foreign key (base_currency) references currency (code),
    add constraint tenant_settings_reporting_currency_fk foreign key (reporting_currency) references currency (code);

-- Banks the company converts with (O-04, C-01): a name and a currency. No account numbers, no bank connection.
-- Plans (Phase 4) reference banks by id, so a rename updates every reference. Removal is soft so past plans keep
-- their bank.
create table bank (
    id          uuid primary key default gen_random_uuid(),
    name        text not null check (name = btrim(name) and length(name) between 1 and 120),
    currency    char(3) not null references currency (code) check (currency in ('MXN', 'USD')),
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    removed_at  timestamptz
);

-- Duplicate names within a currency are ignored (case-insensitive) among banks still in use.
create unique index bank_active_name_uq on bank (currency, lower(name)) where removed_at is null;

-- Once onboarding is complete, at least one MXN and one USD bank must remain.
create function bank_keep_one_per_currency() returns trigger
language plpgsql as $$
declare
    affected_currency char(3) := old.currency;
begin
    if (select onboarding_completed_at from tenant_settings where id = 1) is null then
        return null;
    end if;

    if not exists (select 1 from bank where currency = affected_currency and removed_at is null) then
        raise exception 'at least one % bank must remain', affected_currency
            using errcode = 'check_violation';
    end if;
    return null;
end;
$$;

create trigger bank_keep_one_per_currency
    after update of removed_at, currency or delete on bank
    for each row execute function bank_keep_one_per_currency();

-- Completing onboarding needs a bank of each currency (O-S2).
create function tenant_settings_require_banks() returns trigger
language plpgsql as $$
begin
    if new.onboarding_completed_at is not null and old.onboarding_completed_at is null
       and (not exists (select 1 from bank where currency = 'MXN' and removed_at is null)
            or not exists (select 1 from bank where currency = 'USD' and removed_at is null)) then
        raise exception 'onboarding needs at least one MXN bank and one USD bank'
            using errcode = 'check_violation';
    end if;
    return new;
end;
$$;

create trigger tenant_settings_require_banks
    before update of onboarding_completed_at on tenant_settings
    for each row execute function tenant_settings_require_banks();
