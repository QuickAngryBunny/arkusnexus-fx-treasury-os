-- Forecasting (Phase 2): model registry, published forecasts with intervals, and backtest accuracy.
-- Written only by the forecasting worker from this tenant's own rate history; models are deterministic.

create table forecast_model (
    code           text not null check (code ~ '^[a-z][a-z0-9_]{1,40}$'),
    version        text not null,
    name           text not null,
    description    text not null,
    params         jsonb not null default '{}'::jsonb,
    is_benchmark   boolean not null default false,
    registered_at  timestamptz not null default now(),
    primary key (code, version)
);

create table forecast_run (
    id             bigint generated always as identity primary key,
    source_code    text not null references rate_source (code),
    model_code     text not null,
    model_version  text not null,
    as_of_date     date not null,       -- last rate date the forecast used
    last_rate      numeric(18, 6) not null check (last_rate > 0),
    created_at     timestamptz not null default now(),
    foreign key (model_code, model_version) references forecast_model (code, version),
    unique (source_code, model_code, model_version, as_of_date)
);

-- Rates are quote per base (MXN per USD). Every horizon carries an 80% and a 95% interval.
create table forecast_point (
    run_id        bigint not null references forecast_run (id),
    horizon_days  int not null check (horizon_days between 1 and 366),
    target_date   date not null,
    point         numeric(18, 6) not null,
    lower80       numeric(18, 6) not null,
    upper80       numeric(18, 6) not null,
    lower95       numeric(18, 6) not null,
    upper95       numeric(18, 6) not null,
    primary key (run_id, horizon_days),
    check (0 < lower95 and lower95 <= lower80 and lower80 <= point and point <= upper80 and upper80 <= upper95)
);

-- Accuracy of a model per horizon over past forecast origins, side by side with the naive benchmark.
create table backtest_result (
    id             bigint generated always as identity primary key,
    source_code    text not null references rate_source (code),
    model_code     text not null,
    model_version  text not null,
    horizon_days   int not null,
    origins_from   date not null,
    origins_to     date not null,
    n              int not null check (n > 0),
    mae            numeric(18, 6) not null,   -- mean absolute error, in quote currency per base unit
    mape           numeric(10, 6) not null,   -- mean absolute percentage error, percent
    coverage80     numeric(6, 4) not null,    -- share of actuals inside the 80% interval
    coverage95     numeric(6, 4) not null,
    mae_vs_naive   numeric(10, 6) not null,   -- model MAE / naive MAE (< 1 beats the benchmark)
    computed_at    timestamptz not null default now(),
    foreign key (model_code, model_version) references forecast_model (code, version),
    unique (source_code, model_code, model_version, horizon_days, origins_to)
);
