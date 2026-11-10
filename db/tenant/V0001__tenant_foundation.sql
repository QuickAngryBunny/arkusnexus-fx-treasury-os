-- Tenant database foundation (Phase 0).
-- Every tenant database gets this schema. Market data (Phase 1) and treasury entities (Phase 3) follow as new files.

-- Exactly one row; written by the provisioner. Every API connection checks tenant_id against the tenant in context.
create table tenant_settings (
    id             smallint primary key default 1 check (id = 1),
    tenant_id      uuid not null,
    display_name   text not null,
    base_currency  char(3) not null default 'MXN',
    locale         text not null default 'es-MX' check (locale in ('es-MX', 'en-US')),
    time_zone      text not null default 'America/Tijuana',
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

create table currency (
    code         char(3) primary key check (code ~ '^[A-Z]{3}$'),
    name         text not null,
    minor_units  smallint not null default 2 check (minor_units between 0 and 4),
    is_active    boolean not null default true
);

insert into currency (code, name, minor_units) values
    ('MXN', 'Mexican peso', 2),
    ('USD', 'US dollar', 2),
    ('EUR', 'Euro', 2),
    ('CAD', 'Canadian dollar', 2);

create table legal_entity (
    id                   uuid primary key default gen_random_uuid(),
    name                 text not null unique,
    tax_id               text,
    country              char(2) not null check (country ~ '^[A-Z]{2}$'),
    functional_currency  char(3) not null references currency (code),
    created_at           timestamptz not null default now()
);

create table department (
    id               uuid primary key default gen_random_uuid(),
    legal_entity_id  uuid references legal_entity (id),
    name             text not null check (length(name) between 1 and 120),
    created_at       timestamptz not null default now(),
    unique nulls not distinct (legal_entity_id, name)
);

-- People inside the tenant. Login identities move to the control plane with sign-in in Phase 3.
create table user_profile (
    id             uuid primary key default gen_random_uuid(),
    email          text not null unique,
    display_name   text not null,
    role           text not null check (role in ('standard', 'admin')),
    department_id  uuid references department (id),
    created_at     timestamptz not null default now()
);

-- Append-only audit trail (PRD: complete auditability).
create table audit_log (
    id              bigint generated always as identity primary key,
    occurred_at     timestamptz not null default clock_timestamp(),
    actor           text not null,
    action          text not null,
    entity_type     text,
    entity_id       text,
    correlation_id  text,
    data            jsonb not null default '{}'::jsonb
);

create index audit_log_occurred_idx on audit_log (occurred_at desc);
create index audit_log_entity_idx on audit_log (entity_type, entity_id);

create function audit_log_reject_change() returns trigger
language plpgsql as $$
begin
    raise exception 'audit_log is append-only (% blocked)', tg_op;
end;
$$;

create trigger audit_log_no_update_delete
    before update or delete on audit_log
    for each row execute function audit_log_reject_change();

create trigger audit_log_no_truncate
    before truncate on audit_log
    for each statement execute function audit_log_reject_change();
