using Dapper;
using Npgsql;

namespace FxOs.Platform.Alerts;

/// <summary>The forecast Exchange Forecast headlines: the latest run of the first non-benchmark model in display order.</summary>
public sealed record StoredForecast(long RunId, string ModelCode, DateOnly AsOf, decimal LastRate, IReadOnlyList<ForecastHorizon> Horizons)
{
    public ForecastHorizon? Horizon(int days) => Horizons.FirstOrDefault(h => h.Days == days);
}

public static class StoredForecasts
{
    public static async Task<StoredForecast?> PrimaryAsync(NpgsqlConnection conn, string source, CancellationToken ct = default)
    {
        var run = await conn.QueryFirstOrDefaultAsync<RunRow>(new CommandDefinition(
            """
            select id, model_code, as_of_date, last_rate from (
                select distinct on (r.model_code) r.id, r.model_code, r.as_of_date, r.last_rate, m.is_benchmark, m.display_order
                from forecast_run r join forecast_model m on m.code = r.model_code and m.version = r.model_version
                where r.source_code = @source
                order by r.model_code, r.as_of_date desc, r.id desc
            ) latest
            order by is_benchmark, display_order, model_code collate "C"
            limit 1
            """,
            new { source }, cancellationToken: ct));
        if (run is null)
        {
            return null;
        }

        var points = (await conn.QueryAsync<PointRow>(new CommandDefinition(
            "select horizon_days, point, lower80, upper80 from forecast_point where run_id = @id order by horizon_days",
            new { id = run.Id }, cancellationToken: ct)))
            .Select(p => new ForecastHorizon(p.HorizonDays, p.Point, p.Lower80, p.Upper80))
            .ToList();
        return new StoredForecast(run.Id, run.ModelCode, DateOnly.FromDateTime(run.AsOfDate), run.LastRate, points);
    }

    private sealed class RunRow
    {
        public long Id { get; set; }
        public string ModelCode { get; set; } = "";
        public DateTime AsOfDate { get; set; }
        public decimal LastRate { get; set; }
    }

    private sealed class PointRow
    {
        public int HorizonDays { get; set; }
        public decimal Point { get; set; }
        public decimal Lower80 { get; set; }
        public decimal Upper80 { get; set; }
    }
}
