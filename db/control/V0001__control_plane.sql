-- Control plane: only what the app needs before it knows which tenant a request belongs to.
-- Never put tenant financial or market data here.

create table tenant (
    id            uuid primary key default gen_random_uuid(),
    slug          text not null unique check (slug ~ '^[a-z][a-z0-9_]{2,30}$'),
    display_name  text not null check (length(display_name) between 1 and 200),
    status        text not null check (status in ('provisioning', 'active', 'suspended', 'failed')),
    -- Dummy tenants hold generated data only. The seeder refuses to touch a tenant where this is false.
    is_dummy      boolean not null default true,
    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now()
);

-- Routing: where each tenant's database lives and which login opens it.
create table tenant_database (
    tenant_id       uuid primary key references tenant (id),
    -- Key into the TenantServers configuration section, so hosts can differ per environment.
    server_key      text not null default 'default',
    database_name   text not null unique,
    login_role      text not null unique,
    -- Reference into the secret store (dev_secret in Phase 0, Azure Key Vault later). Never the password itself.
    secret_ref      text not null unique,
    schema_version  int  not null default 0,
    migrated_at     timestamptz
);

create table provisioning_event (
    id          bigint generated always as identity primary key,
    tenant_id   uuid references tenant (id),
    step        text not null,
    outcome     text not null check (outcome in ('succeeded', 'failed')),
    detail      text,
    occurred_at timestamptz not null default now()
);

create index provisioning_event_tenant_idx on provisioning_event (tenant_id, occurred_at desc);

-- DEVELOPMENT ONLY. Holds generated tenant login passwords on internal environments.
-- The API refuses to start with this store outside Development, Testing and Internal.
create table dev_secret (
    secret_ref    text primary key,
    secret_value  text not null,
    created_at    timestamptz not null default now()
);

comment on table dev_secret is 'Development only; replaced by Azure Key Vault before any non-internal environment.';
