-- Forecast vs actual uses only forecasts the app published on their day (after each Banxico sync), never runs
-- recreated afterwards for past days. Removes those recreated runs and the column that told them apart.
update rate_alert set checked_run_id = null
where checked_run_id in (select id from forecast_run where origin = 'recreated');
update rate_alert_event set forecast_run_id = null
where forecast_run_id in (select id from forecast_run where origin = 'recreated');
delete from forecast_point where run_id in (select id from forecast_run where origin = 'recreated');
delete from forecast_run where origin = 'recreated';
alter table forecast_run drop column origin;
