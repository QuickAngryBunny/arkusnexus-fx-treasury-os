-- Short-term interest rates per currency (Phase 2), used to compute market forwards (interest-rate parity).
-- Public data, loaded only by the ingestion worker into every tenant, like fx_rate.

create table interest_series (
    code        text primary key check (code ~ '^[a-z][a-z0-9_]{1,40}$'),
    name        text not null,
    provider    text not null check (provider in ('banxico', 'us_treasury')),
    series_id   text not null,
    currency    char(3) not null references currency (code),
    tenor_days  int not null check (tenor_days > 0),
    day_count   int not null check (day_count in (360, 365)),  -- money-market basis for simple interest
    is_enabled  boolean not null default true,
    created_at  timestamptz not null default now(),
    unique (provider, series_id)
);

insert into interest_series (code, name, provider, series_id, currency, tenor_days, day_count) values
    ('tiie_28',  'TIIE 28 días',  'banxico', 'SF43783',  'MXN', 28,  360),
    ('tiie_91',  'TIIE 91 días',  'banxico', 'SF43878',  'MXN', 91,  360),
    ('tiie_182', 'TIIE 182 días', 'banxico', 'SF111916', 'MXN', 182, 360),
    ('ust_1m',   'US Treasury 1 month',  'us_treasury', '1 Mo', 'USD', 30,  365),
    ('ust_2m',   'US Treasury 2 months', 'us_treasury', '2 Mo', 'USD', 61,  365),
    ('ust_3m',   'US Treasury 3 months', 'us_treasury', '3 Mo', 'USD', 91,  365),
    ('ust_4m',   'US Treasury 4 months', 'us_treasury', '4 Mo', 'USD', 122, 365),
    ('ust_6m',   'US Treasury 6 months', 'us_treasury', '6 Mo', 'USD', 182, 365),
    ('ust_1y',   'US Treasury 1 year',   'us_treasury', '1 Yr', 'USD', 365, 365);

-- Annual rate in percent, as published.
create table interest_rate (
    series_code  text not null references interest_series (code),
    rate_date    date not null,
    rate_pct     numeric(9, 6) not null check (rate_pct > -10 and rate_pct < 100),
    revision     int not null default 1 check (revision >= 1),
    raw_ref      text not null,
    loaded_at    timestamptz not null default now(),
    primary key (series_code, rate_date)
);

select create_hypertable('interest_rate', by_range('rate_date', interval '5 years'));

-- Order in which the app shows models (lowest first); the first non-benchmark model is the headline forecast.
alter table forecast_model add column display_order int not null default 100;
