-- Exchange-rate history (Phase 1). Each tenant keeps its own copy of public rates (ADR 0001).
-- Rows are loaded only by the ingestion worker from the immutable raw store; user requests never call a source.

create extension if not exists timescaledb;

-- Where a series comes from. One row per series the tenant can load.
create table rate_source (
    code            text primary key check (code ~ '^[a-z][a-z0-9_]{1,40}$'),
    name            text not null,
    provider        text not null check (provider in ('banxico', 'dof')),
    series_id       text not null,
    base_currency   char(3) not null references currency (code),
    quote_currency  char(3) not null references currency (code),
    is_enabled      boolean not null default true,
    created_at      timestamptz not null default now(),
    check (base_currency <> quote_currency)
);

insert into rate_source (code, name, provider, series_id, base_currency, quote_currency) values
    ('banxico_fix', 'Banxico FIX (fecha de determinación)', 'banxico', 'SF43718', 'USD', 'MXN');

-- One rate per source and day: quote currency units per 1 unit of base (MXN per USD for FIX).
-- A rerun of the same load changes nothing; a source revising a past value bumps `revision` and is audited.
create table fx_rate (
    source_code  text not null references rate_source (code),
    rate_date    date not null,
    rate         numeric(18, 6) not null check (rate > 0),
    revision     int not null default 1 check (revision >= 1),
    raw_ref      text not null,  -- raw-store object the current value was loaded from
    loaded_at    timestamptz not null default now(),
    primary key (source_code, rate_date)
);

select create_hypertable('fx_rate', by_range('rate_date', interval '5 years'));

-- Every load attempt for this tenant, for the sync panel and troubleshooting.
create table rate_load (
    id              bigint generated always as identity primary key,
    source_code     text not null references rate_source (code),
    raw_ref         text not null,
    raw_sha256      char(64) not null,
    range_from      date,
    range_to        date,
    rows_seen       int not null default 0,
    rows_inserted   int not null default 0,
    rows_revised    int not null default 0,
    rows_unchanged  int not null default 0,
    rows_skipped    int not null default 0,  -- source published no value (N/E)
    status          text not null check (status in ('succeeded', 'failed')),
    error           text,
    started_at      timestamptz not null,
    finished_at     timestamptz not null default now()
);

create index rate_load_source_idx on rate_load (source_code, finished_at desc);
