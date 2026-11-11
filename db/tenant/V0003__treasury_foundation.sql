-- Treasury foundation: bank accounts and their income/expense transactions.
-- Phases 0–2 hold dummy data only (written by workers/python, which refuses non-dummy tenants).
-- Phase 3 adds imports, invoices, payments and reconciliation on top of these tables.

create table bank_account (
    id                    uuid primary key default gen_random_uuid(),
    legal_entity_id       uuid not null references legal_entity (id),
    bank_name             text not null,
    account_name          text not null,
    account_last4         char(4) not null check (account_last4 ~ '^[0-9]{4}$'),
    currency              char(3) not null references currency (code),
    kind                  text not null check (kind in ('operating', 'payroll', 'savings')),
    opening_balance       numeric(18, 2) not null default 0,
    opening_balance_date  date not null,
    -- Bank feed connection. Phases 0–2 use the 'dummy' feed; real providers come with Phase 3.
    feed_provider         text check (feed_provider in ('dummy')),
    feed_status           text not null default 'disconnected'
                          check (feed_status in ('connected', 'needs_attention', 'disconnected')),
    last_synced_at        timestamptz,
    created_at            timestamptz not null default now(),
    unique (legal_entity_id, account_name)
);

-- One booked bank movement, in the account's own currency (original amount, never converted here).
create table financial_transaction (
    id               uuid primary key default gen_random_uuid(),
    bank_account_id  uuid not null references bank_account (id),
    booked_on        date not null,
    direction        text not null check (direction in ('income', 'expense')),
    amount           numeric(18, 2) not null check (amount > 0),
    currency         char(3) not null references currency (code),
    category         text not null check (category in (
                         'client_payment', 'intercompany', 'other_income',
                         'payroll', 'year_end_bonus', 'social_security', 'taxes', 'rent', 'utilities',
                         'cloud_software', 'contractors', 'travel', 'bank_fees', 'other_expense',
                         'fx_conversion')),
    counterparty     text,
    description      text not null,
    source           text not null check (source in ('dummy_feed', 'import', 'manual')),
    -- Currency conversions: two legs (one per account) share conversion_id. Each keeps its own original amount and
    -- records the other leg, the applied rate (quote per base, e.g. MXN per USD), where it came from and when.
    conversion_id     uuid,
    counter_amount    numeric(18, 2) check (counter_amount > 0),
    counter_currency  char(3) references currency (code),
    fx_rate           numeric(18, 6) check (fx_rate > 0),
    fx_rate_source    text,
    fx_rate_at        timestamptz,
    created_at       timestamptz not null default now(),
    check ((category = 'fx_conversion') = (conversion_id is not null)),
    check (conversion_id is null or (counter_amount is not null and counter_currency is not null
           and counter_currency <> currency and fx_rate is not null and fx_rate_source is not null
           and fx_rate_at is not null))
);

create index financial_transaction_conversion_idx on financial_transaction (conversion_id) where conversion_id is not null;

create index financial_transaction_account_idx on financial_transaction (bank_account_id, booked_on desc);
create index financial_transaction_booked_idx on financial_transaction (booked_on desc);

-- A transaction is always in its account's currency.
create function financial_transaction_check_currency() returns trigger
language plpgsql as $$
begin
    if new.currency <> (select currency from bank_account where id = new.bank_account_id) then
        raise exception 'transaction currency % does not match its bank account', new.currency;
    end if;
    return new;
end;
$$;

create trigger financial_transaction_currency
    before insert or update on financial_transaction
    for each row execute function financial_transaction_check_currency();
