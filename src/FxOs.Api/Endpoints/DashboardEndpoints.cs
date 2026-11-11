using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Executive dashboard (P8-01 "Glance", P8-02 "Focus with filters"). Deterministic figures from the tenant's own data:
/// treasury balances and flows (dummy in Phases 0–2) combined with the real Banxico FIX and the forward model.
/// Every converted figure carries the FIX it used (rate, date); originals stay in their own currency.
/// </summary>
public static class DashboardEndpoints
{
    private static readonly string[] Transfers = TreasuryEndpoints.TransferCategories;
    private const int ProjectionSteps = 6;    // six two-week steps ≈ 90 days
    private const int StepDays = 14;

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenant/dashboard", async (
            string? period, string? currencies, string? customer, Guid? entity,
            ITenantConnectionFactory db, CancellationToken ct) =>
        {
            var p = period is "quarter" or "12m" ? period : "month";
            var wanted = (currencies ?? "USD,MXN").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant()).Where(c => c.Length == 3).ToHashSet();
            if (wanted.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencies"] = ["Choose at least one currency."] });
            }

            await using var conn = await db.OpenAsync(ct);

            var fixRows = (await conn.QueryAsync<(DateTime Date, decimal Rate)>(new CommandDefinition(
                "select rate_date, rate from fx_rate where source_code = 'banxico_fix' order by rate_date", cancellationToken: ct))).ToList();
            if (fixRows.Count == 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No exchange rates yet",
                    detail: "Load the Banxico FIX before opening the dashboard.");
            }

            var fixDates = fixRows.Select(r => DateOnly.FromDateTime(r.Date)).ToArray();
            var fixRates = fixRows.Select(r => r.Rate).ToArray();
            var fixDate = fixDates[^1];
            var fix = fixRates[^1];

            var accounts = (await conn.QueryAsync<AccountRow>(new CommandDefinition(
                """
                select a.id, a.legal_entity_id, le.name as entity_name, le.functional_currency as entity_currency, a.currency,
                       a.opening_balance
                from bank_account a join legal_entity le on le.id = a.legal_entity_id
                """, cancellationToken: ct))).ToList();
            var txns = (await conn.QueryAsync<TxnRow>(new CommandDefinition(
                "select bank_account_id, booked_on, direction, amount, currency, category, counterparty from financial_transaction",
                cancellationToken: ct))).ToList();

            var scoped = accounts.Where(a => wanted.Contains(a.Currency) && (entity is null || a.LegalEntityId == entity)).ToList();
            var scopedIds = scoped.Select(a => a.Id).ToHashSet();
            var scopedTxns = txns.Where(t => scopedIds.Contains(t.BankAccountId)).ToList();
            var today = scopedTxns.Count > 0 ? DateOnly.FromDateTime(scopedTxns.Max(t => t.BookedOn)) : fixDate;
            if (fixDate > today)
            {
                today = fixDate;
            }

            decimal Signed(TxnRow t) => t.Direction == "income" ? t.Amount : -t.Amount;
            decimal BalanceAt(AccountRow a, DateOnly d) =>
                a.OpeningBalance + txns.Where(t => t.BankAccountId == a.Id && DateOnly.FromDateTime(t.BookedOn) <= d).Sum(Signed);
            decimal ToUsd(decimal amount, string currency, decimal rate) => currency switch
            {
                "USD" => amount,
                "MXN" => amount / rate,
                _ => 0m, // only USD and MXN accounts exist today; other currencies need their own rate series
            };

            // ── Cash position (today's FIX for both dates, so the change is cash, not the rate) ──
            var prevMonthEnd = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
            var byCurrency = scoped.GroupBy(a => a.Currency).OrderBy(g => g.Key != "USD").ThenBy(g => g.Key).Select(g => new
            {
                Currency = g.Key,
                Balance = g.Sum(a => BalanceAt(a, today)),
                PrevMonthEnd = g.Sum(a => BalanceAt(a, prevMonthEnd)),
            }).ToList();
            var totalUsd = byCurrency.Sum(c => ToUsd(c.Balance, c.Currency, fix));
            var prevUsd = byCurrency.Sum(c => ToUsd(c.PrevMonthEnd, c.Currency, fix));

            // ── Net USD exposure: dollars held by entities whose functional currency is the peso. No hedges exist yet. ──
            var exposureUsd = scoped.Where(a => a.Currency == "USD" && a.EntityCurrency == "MXN").Sum(a => BalanceAt(a, today));

            // ── Revenue and expenses in the period, converted at the period's average FIX ──
            var from = p switch
            {
                "quarter" => new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1),
                "12m" => today.AddDays(-364),
                _ => new DateOnly(today.Year, today.Month, 1),
            };
            var inPeriod = scopedTxns.Where(t => DateOnly.FromDateTime(t.BookedOn) >= from && DateOnly.FromDateTime(t.BookedOn) <= today
                                                 && !Transfers.Contains(t.Category)).ToList();
            var periodFix = AverageFix(fixDates, fixRates, from, today) ?? fix;
            var revenueTxns = inPeriod.Where(t => t.Direction == "income" && (customer is null || (t.Category == "client_payment" && t.Counterparty == customer))).ToList();
            var expenseTxns = customer is null ? inPeriod.Where(t => t.Direction == "expense").ToList() : [];
            var revenueUsd = revenueTxns.Sum(t => ToUsd(t.Amount, t.Currency, periodFix));
            var expensesUsd = expenseTxns.Sum(t => ToUsd(t.Amount, t.Currency, periodFix));
            var allRevenueUsd = inPeriod.Where(t => t.Direction == "income").Sum(t => ToUsd(t.Amount, t.Currency, periodFix));

            // ── Peso cash: today's balance, then two-week steps estimated from the same weeks last year ──
            var mxn = wanted.Contains("MXN") ? scoped.Where(a => a.Currency == "MXN").ToList() : [];
            var mxnIds = mxn.Select(a => a.Id).ToHashSet();
            var mxnTxns = txns.Where(t => mxnIds.Contains(t.BankAccountId)).ToList();
            object? projection = null;
            decimal? reserve = null;
            (DateOnly Date, decimal Balance)? lowest = null;
            if (mxn.Count > 0)
            {
                // Reserve = one month of peso costs (average of the last three full months, rounded to MX$100K).
                var threeMonthsAgo = new DateOnly(today.Year, today.Month, 1).AddMonths(-3);
                var monthlyCost = mxnTxns.Where(t => t.Direction == "expense" && !Transfers.Contains(t.Category)
                        && DateOnly.FromDateTime(t.BookedOn) >= threeMonthsAgo && DateOnly.FromDateTime(t.BookedOn) <= prevMonthEnd)
                    .Sum(t => t.Amount) / 3m;
                reserve = Math.Round(monthlyCost / 100_000m, MidpointRounding.AwayFromZero) * 100_000m;

                var balance = mxn.Sum(a => BalanceAt(a, today));
                var steps = new List<object> { new { Date = today, Balance = balance, Kind = "actual", BelowReserve = balance < reserve } };
                lowest = (today, balance);
                for (var k = 1; k <= ProjectionSteps; k++)
                {
                    var stepEnd = today.AddDays(k * StepDays);
                    var lastYearFrom = today.AddDays(((k - 1) * StepDays) - 364);
                    var lastYearTo = today.AddDays((k * StepDays) - 364);
                    balance += mxnTxns.Where(t => DateOnly.FromDateTime(t.BookedOn) > lastYearFrom && DateOnly.FromDateTime(t.BookedOn) <= lastYearTo).Sum(Signed);
                    steps.Add(new { Date = stepEnd, Balance = balance, Kind = "estimated", BelowReserve = balance < reserve });
                    if (balance < lowest.Value.Balance)
                    {
                        lowest = (stepEnd, balance);
                    }
                }

                projection = new { ReserveMxn = reserve, Method = "same_weeks_last_year", Steps = steps };
            }

            // ── Forward model: rate for a date (nearest published horizon on or after it) ──
            var forward = (await conn.QueryAsync<(int HorizonDays, DateTime TargetDate, decimal Point, decimal Lower80, decimal Upper80)>(new CommandDefinition(
                """
                select p.horizon_days, p.target_date, p.point, p.lower80, p.upper80
                from forecast_point p join forecast_run r on r.id = p.run_id
                where r.id = (select id from forecast_run where source_code = 'banxico_fix' and model_code = 'forward'
                              order by as_of_date desc, id desc limit 1)
                order by p.horizon_days
                """, cancellationToken: ct))).ToList();

            object recommendation;
            if (reserve is not null && lowest is not null && lowest.Value.Balance < reserve)
            {
                var shortfall = reserve.Value - lowest.Value.Balance;
                var f = forward.FirstOrDefault(x => DateOnly.FromDateTime(x.TargetDate) >= lowest.Value.Date);
                var rate = f.HorizonDays > 0 ? f.Point : fix;
                recommendation = new
                {
                    Kind = "liquidity",
                    Date = lowest.Value.Date,
                    ShortfallMxn = Math.Round(shortfall, 2),
                    UsdToConvert = Math.Round(shortfall / rate, 2),
                    Rate = rate,
                    RateSource = f.HorizonDays > 0 ? $"forward {f.HorizonDays}D" : "FIX",
                };
            }
            else
            {
                var f90 = forward.FirstOrDefault(x => x.HorizonDays == 90);
                recommendation = new
                {
                    Kind = exposureUsd > 0 ? "exposure" : "none",
                    ExposureUsd = Math.Round(exposureUsd, 2),
                    MxnAtFix = Math.Round(exposureUsd * fix, 2),
                    Horizon = f90.HorizonDays > 0 ? f90.HorizonDays : (int?)null,
                    MxnLow80 = f90.HorizonDays > 0 ? Math.Round(exposureUsd * f90.Lower80, 2) : (decimal?)null,
                    MxnHigh80 = f90.HorizonDays > 0 ? Math.Round(exposureUsd * f90.Upper80, 2) : (decimal?)null,
                };
            }

            return Results.Ok(new
            {
                AsOf = today,
                Fix = new { Date = fixDate, Rate = fix, Source = "Banxico SIE SF43718" },
                Filters = new { Period = p, Currencies = wanted.Order(), Customer = customer, Entity = entity },
                Cash = new
                {
                    ByCurrency = byCurrency.Select(c => new { c.Currency, c.Balance }),
                    TotalUsd = Math.Round(totalUsd, 2),
                    PrevMonthEnd = prevMonthEnd,
                    ChangePct = prevUsd == 0 ? (decimal?)null : Math.Round((totalUsd - prevUsd) / prevUsd * 100m, 2),
                },
                Exposure = new { UsdLong = Math.Round(exposureUsd, 2), HedgedPct = 0m },
                Flows = new
                {
                    From = from,
                    To = today,
                    Fix = Math.Round(periodFix, 6),
                    RevenueUsd = Math.Round(revenueUsd, 2),
                    ExpensesUsd = customer is null ? Math.Round(expensesUsd, 2) : (decimal?)null,
                    MarginPct = customer is null && revenueUsd > 0 ? Math.Round((revenueUsd - expensesUsd) / revenueUsd * 100m, 1) : (decimal?)null,
                    CustomerSharePct = customer is not null && allRevenueUsd > 0 ? Math.Round(revenueUsd / allRevenueUsd * 100m, 1) : (decimal?)null,
                },
                PesoProjection = projection,
                Recommendation = recommendation,
                Options = new
                {
                    Customers = txns.Where(t => t.Category == "client_payment" && t.Counterparty is not null)
                        .Select(t => t.Counterparty!).Distinct().Order(StringComparer.Ordinal),
                    Entities = accounts.Select(a => new { Id = a.LegalEntityId, Name = a.EntityName }).DistinctBy(e => e.Id).OrderBy(e => e.Name),
                    Currencies = accounts.Select(a => a.Currency).Distinct().Order(),
                },
            });
        }).WithTags("Dashboard");
    }

    private static decimal? AverageFix(DateOnly[] dates, decimal[] rates, DateOnly from, DateOnly to)
    {
        var inRange = Enumerable.Range(0, dates.Length).Where(i => dates[i] >= from && dates[i] <= to).Select(i => rates[i]).ToList();
        return inRange.Count == 0 ? null : inRange.Average();
    }
}

internal sealed class AccountRow
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string EntityName { get; set; } = "";
    public string EntityCurrency { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal OpeningBalance { get; set; }
}

internal sealed class TxnRow
{
    public Guid BankAccountId { get; set; }
    public DateTime BookedOn { get; set; }
    public string Direction { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Counterparty { get; set; }
}
