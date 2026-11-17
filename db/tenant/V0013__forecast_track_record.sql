-- Forecast vs actual in Full history (R-02). Past forecasts are compared with the FIX that followed them.
-- origin: 'published' = the run the app showed on its as-of date (fxworkers forecast run);
--         'recreated' = computed afterwards by `fxworkers forecast recreate` for a past as-of date, using only the
--                       rates and interest-rate curves available on that date (the same walk-forward rule as the
--                       backtest). It fills days the app had not published yet; a published run is never replaced.
alter table forecast_run add column origin text not null default 'published' check (origin in ('published', 'recreated'));

-- Finding "the forecast made N days before this FIX": by horizon and target date.
create index forecast_point_target_idx on forecast_point (horizon_days, target_date);
