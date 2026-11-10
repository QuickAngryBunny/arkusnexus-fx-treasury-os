using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Read-only treasury data: bank accounts (with balance and feed status), their movements and a monthly
/// income/expense summary. Amounts stay in each account's own currency; nothing is converted here.
/// Phases 0–2 serve dummy data only; Phase 3 adds imports, invoices and reconciliation.
/// </summary>
public static class TreasuryEndpoints
{
    /// <summary>Categories that move cash between the company's own accounts or currencies, not income or cost.</summary>
    public static readonly string[] TransferCategories = ["intercompany", "fx_conversion"];

    public static RouteGroupBuilder MapTreasuryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant").WithTags("Treasury");

        group.MapGet("/bank-accounts", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<BankAccountRow>(new CommandDefinition(
                """
                select a.id, a.legal_entity_id, le.name as legal_entity_name, a.bank_name, a.account_name,
                       a.account_last4, a.currency, a.kind, a.feed_provider, a.feed_status, a.last_synced_at,
                       a.opening_balance + coalesce(m.net, 0) as balance, m.last_booked_on
                from bank_account a
                join legal_entity le on le.id = a.legal_entity_id
                left join lateral (
                    select sum(case direction when 'income' then amount else -amount end) as net,
                           max(booked_on) as last_booked_on
                    from financial_transaction where bank_account_id = a.id
                ) m on true
                order by le.name, a.currency, a.account_name
                """,
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new
            {
                r.Id,
                r.LegalEntityId,
                r.LegalEntityName,
                r.BankName,
                r.AccountName,
                r.AccountLast4,
                r.Currency,
                r.Kind,
                Feed = new { Provider = r.FeedProvider, Status = r.FeedStatus, r.LastSyncedAt },
                r.Balance,
                LastBookedOn = r.LastBookedOn is null ? (DateOnly?)null : DateOnly.FromDateTime(r.LastBookedOn.Value),
            }));
        });

        group.MapGet("/transactions", async (
            DateOnly? from, DateOnly? to, Guid? accountId, string? direction, int? limit,
            ITenantConnectionFactory db, CancellationToken ct) =>
        {
            if (direction is not (null or "income" or "expense"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["direction"] = ["Use 'income' or 'expense'."],
                });
            }

            var take = Math.Clamp(limit ?? 100, 1, 500);
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<TransactionRow>(new CommandDefinition(
                """
                select t.id, t.bank_account_id, a.account_name, t.booked_on, t.direction, t.amount, t.currency,
                       t.category, t.counterparty, t.description, t.source, t.conversion_id, t.counter_amount,
                       t.counter_currency, t.fx_rate, t.fx_rate_source, t.fx_rate_at
                from financial_transaction t
                join bank_account a on a.id = t.bank_account_id
                where (@from::date is null or t.booked_on >= @from::date)
                  and (@to::date is null or t.booked_on <= @to::date)
                  and (@accountId::uuid is null or t.bank_account_id = @accountId::uuid)
                  and (@direction::text is null or t.direction = @direction::text)
                order by t.booked_on desc, t.id
                limit @take
                """,
                new
                {
                    from = from?.ToDateTime(TimeOnly.MinValue),
                    to = to?.ToDateTime(TimeOnly.MinValue),
                    accountId,
                    direction,
                    take,
                },
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new
            {
                r.Id,
                r.BankAccountId,
                r.AccountName,
                BookedOn = DateOnly.FromDateTime(r.BookedOn),
                r.Direction,
                r.Amount,
                r.Currency,
                r.Category,
                r.Counterparty,
                r.Description,
                r.Source,
                Conversion = r.ConversionId is null
                    ? null
                    : new
                    {
                        Id = r.ConversionId,
                        CounterAmount = r.CounterAmount,
                        CounterCurrency = r.CounterCurrency,
                        AppliedRate = r.FxRate,
                        RateSource = r.FxRateSource,
                        RateAt = r.FxRateAt,
                    },
            }));
        });

        group.MapGet("/cash-flow/monthly", async (int? months, ITenantConnectionFactory db, CancellationToken ct) =>
        {
            var span = Math.Clamp(months ?? 12, 1, 60);
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<MonthRow>(new CommandDefinition(
                """
                with bounds as (
                    select date_trunc('month', coalesce(max(booked_on), current_date))::date as last_month
                    from financial_transaction
                )
                select to_char(date_trunc('month', t.booked_on), 'YYYY-MM') as month, t.currency,
                       sum(t.amount) filter (where t.direction = 'income' and not t.category = any(@transfers)) as income,
                       sum(t.amount) filter (where t.direction = 'expense' and not t.category = any(@transfers)) as expense,
                       sum(t.amount) filter (where t.direction = 'income' and t.category = any(@transfers)) as transfers_in,
                       sum(t.amount) filter (where t.direction = 'expense' and t.category = any(@transfers)) as transfers_out
                from financial_transaction t, bounds b
                where t.booked_on >= b.last_month - make_interval(months => @span - 1)
                group by 1, 2
                order by 1, 2
                """,
                new { transfers = TransferCategories, span },
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new
            {
                r.Month,
                r.Currency,
                Income = r.Income ?? 0m,
                Expense = r.Expense ?? 0m,
                Net = (r.Income ?? 0m) - (r.Expense ?? 0m),
                TransfersIn = r.TransfersIn ?? 0m,
                TransfersOut = r.TransfersOut ?? 0m,
            }));
        });

        return group;
    }
}

internal sealed class BankAccountRow
{
    public Guid Id { get; set; }
    public Guid LegalEntityId { get; set; }
    public string LegalEntityName { get; set; } = "";
    public string BankName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountLast4 { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? FeedProvider { get; set; }
    public string FeedStatus { get; set; } = "";
    public DateTime? LastSyncedAt { get; set; }
    public decimal Balance { get; set; }
    public DateTime? LastBookedOn { get; set; }
}

internal sealed class TransactionRow
{
    public Guid Id { get; set; }
    public Guid BankAccountId { get; set; }
    public string AccountName { get; set; } = "";
    public DateTime BookedOn { get; set; }
    public string Direction { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Counterparty { get; set; }
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public Guid? ConversionId { get; set; }
    public decimal? CounterAmount { get; set; }
    public string? CounterCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public string? FxRateSource { get; set; }
    public DateTime? FxRateAt { get; set; }
}

internal sealed class MonthRow
{
    public string Month { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal? Income { get; set; }
    public decimal? Expense { get; set; }
    public decimal? TransfersIn { get; set; }
    public decimal? TransfersOut { get; set; }
}
