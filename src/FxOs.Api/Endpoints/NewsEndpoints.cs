using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// USD/MXN news for Forecast Intelligence: flagged moves (beyond ±2σ of the previous 30 days) and the GNews headlines
/// the ingestion worker stored for today and for each move. Reads the tenant database only: never calls GNews.
/// </summary>
public static class NewsEndpoints
{
    private const int Window = 21; // business days ≈ 30 calendar days, same rule as the worker
    private const double TradingDays = 252;

    public static void MapNewsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenant/news", async (DateOnly? from, DateOnly? to, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
            var start = from ?? end.AddDays(-366);
            if (start > end)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["'from' must be on or before 'to'."] });
            }

            var p = new { start = start.ToDateTime(TimeOnly.MinValue), end = end.ToDateTime(TimeOnly.MinValue) };
            var moves = await conn.QueryAsync<MoveRow>(new CommandDefinition(
                """
                select rate_date, rate, change_pct, sigma, vol30_pct from rate_move
                where source_code = 'banxico_fix' and rate_date between @start and @end order by rate_date
                """, p, cancellationToken: ct));
            var windows = (await conn.QueryAsync<WindowRow>(new CommandDefinition(
                """
                select window_key, kind, rate_date, window_from, window_to, fetched_at from news_window
                where rate_date between @start and @end order by rate_date desc
                """, p, cancellationToken: ct))).ToList();
            var articles = (await conn.QueryAsync<ArticleRow>(new CommandDefinition(
                """
                select window_key, rank, lang, title, description, url, source_name, source_url, published_at
                from news_article where window_key = any(@keys) order by window_key, rank
                """, new { keys = windows.Select(w => w.WindowKey).ToArray() }, cancellationToken: ct))).ToLookup(a => a.WindowKey);

            // Today's move scored with the same rule as flagged moves (for the "what's happening" card).
            var recent = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                """
                select rate_date, rate from (select rate_date, rate from fx_rate where source_code = 'banxico_fix'
                                             order by rate_date desc limit @n) r order by rate_date
                """, new { n = Window + 2 }, cancellationToken: ct))).ToList();

            return Results.Ok(new
            {
                Latest = Score(recent),
                Moves = moves.Select(m => new
                {
                    Date = DateOnly.FromDateTime(m.RateDate), m.Rate, m.ChangePct, m.Sigma, m.Vol30Pct,
                }),
                Windows = windows.Select(w => new
                {
                    Key = w.WindowKey,
                    w.Kind,
                    RateDate = DateOnly.FromDateTime(w.RateDate),
                    From = w.WindowFrom,
                    To = w.WindowTo,
                    w.FetchedAt,
                    Articles = articles[w.WindowKey].Select(a => new
                    {
                        a.Rank, Lang = a.Lang.Trim(), a.Title, a.Description, a.Url, a.SourceName, a.SourceUrl, a.PublishedAt,
                    }),
                }),
            });
        }).WithTags("News");
    }

    private static object? Score(List<(DateTime Date, decimal Rate)> rows)
    {
        if (rows.Count < Window + 2)
        {
            return null;
        }

        var rets = new List<double>();
        for (var k = rows.Count - 1 - Window; k < rows.Count - 1; k++)
        {
            rets.Add(Math.Log((double)rows[k].Rate / (double)rows[k - 1].Rate));
        }

        var mean = rets.Average();
        var sd = Math.Sqrt(rets.Sum(r => (r - mean) * (r - mean)) / (Window - 1));
        var last = rows[^1];
        var prev = rows[^2];
        var ret = Math.Log((double)last.Rate / (double)prev.Rate);
        return new
        {
            Date = DateOnly.FromDateTime(last.Date),
            last.Rate,
            PreviousDate = DateOnly.FromDateTime(prev.Date),
            ChangePct = Math.Round((last.Rate / prev.Rate - 1m) * 100m, 4),
            Sigma = sd == 0 ? 0 : Math.Round(ret / sd, 2),
            Vol30Pct = Math.Round(sd * Math.Sqrt(TradingDays) * 100, 2),
        };
    }
}

internal sealed class MoveRow
{
    public DateTime RateDate { get; set; }
    public decimal Rate { get; set; }
    public decimal ChangePct { get; set; }
    public decimal Sigma { get; set; }
    public decimal Vol30Pct { get; set; }
}

internal sealed class WindowRow
{
    public string WindowKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime RateDate { get; set; }
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    public DateTime FetchedAt { get; set; }
}

internal sealed class ArticleRow
{
    public string WindowKey { get; set; } = "";
    public int Rank { get; set; }
    public string Lang { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Url { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string? SourceUrl { get; set; }
    public DateTime PublishedAt { get; set; }
}
