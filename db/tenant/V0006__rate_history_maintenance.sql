-- Full history maintenance (R-02): resolutions for missing days and manual sync requests.
-- The API never calls a rate source: "Sync now" records a request that the ingestion worker picks up.

-- A business day without a rate that a person has explained (e.g. an unlisted bank holiday).
create table rate_gap_resolution (
    source_code  text not null references rate_source (code),
    rate_date    date not null,
    resolution   text not null check (resolution in ('holiday')),
    note         text,
    resolved_by  text not null,
    resolved_at  timestamptz not null default now(),
    primary key (source_code, rate_date)
);

-- A request to sync a source now (optionally a date range), processed by the worker's scheduler.
create table sync_request (
    id            bigint generated always as identity primary key,
    source_code   text not null references rate_source (code),
    range_from    date,
    range_to      date,
    requested_by  text not null,
    requested_at  timestamptz not null default now(),
    status        text not null default 'pending' check (status in ('pending', 'running', 'done', 'failed')),
    started_at    timestamptz,
    finished_at   timestamptz,
    result        text,
    check (range_from is null or range_to is null or range_from <= range_to)
);

create index sync_request_pending_idx on sync_request (requested_at) where status = 'pending';
