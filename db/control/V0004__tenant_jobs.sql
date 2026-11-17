-- Phase 3 Sprint 3: tenant admin portal (A-01 to A-04, A-S2, A-S3).

-- Deleted tenants (dummy only; real-data tenants can only be archived) keep their row, because the control-plane
-- audit log refers to them and is append-only. Their database, login and routing are gone.
alter table tenant drop constraint tenant_status_check;
alter table tenant add constraint tenant_status_check
    check (status in ('provisioning', 'active', 'suspended', 'failed', 'archived', 'deleted'));
alter table tenant add column deleted_at timestamptz;
alter table tenant add constraint tenant_deleted_at_matches_status
    check ((status = 'deleted') = (deleted_at is not null));

-- Background work on one tenant, shown live in the portal.
--   setup:       database  (API)  → rate_history (Python worker, from the raw store) → invitation (API) → done
--   load_dummy / clear_dummy: one step, run by the Python worker (dummy data only ever comes from workers/python).
-- A failed job keeps its step; Retry sets it back to pending and the owner of that step picks it up again.
create table tenant_job (
    id                 uuid primary key default gen_random_uuid(),
    tenant_id          uuid not null references tenant (id),
    kind               text not null check (kind in ('setup', 'load_dummy', 'clear_dummy')),
    step               text not null check (step in ('database', 'rate_history', 'invitation', 'work', 'done')),
    status             text not null default 'pending' check (status in ('pending', 'running', 'failed', 'done')),
    -- 0–100 within the current step (the rate history copy reports it).
    progress           smallint not null default 0 check (progress between 0 and 100),
    error              text,
    -- Setup only: who gets the setup invitation (O-01).
    first_admin_email  text check (first_admin_email is null or first_admin_email = lower(btrim(first_admin_email))),
    requested_by       uuid references user_account (id),
    -- [{ "step": "database", "at": "...", "note": "..." }, ...]: what finished when, for the progress page.
    events             jsonb not null default '[]'::jsonb,
    created_at         timestamptz not null default now(),
    updated_at         timestamptz not null default now(),
    finished_at        timestamptz,
    check (kind = 'setup' or step in ('work', 'done')),
    check (kind <> 'setup' or step <> 'work'),
    check (kind <> 'setup' or first_admin_email is not null),
    check ((status = 'failed') = (error is not null)),
    check ((status = 'done') = (finished_at is not null))
);

-- One setup per tenant; one unfinished dummy-data job per tenant at a time.
create unique index tenant_job_one_setup_uq on tenant_job (tenant_id) where kind = 'setup';
create unique index tenant_job_one_open_dummy_uq on tenant_job (tenant_id)
    where kind in ('load_dummy', 'clear_dummy') and status in ('pending', 'running', 'failed');
create index tenant_job_due_idx on tenant_job (step, status) where status in ('pending', 'running');
