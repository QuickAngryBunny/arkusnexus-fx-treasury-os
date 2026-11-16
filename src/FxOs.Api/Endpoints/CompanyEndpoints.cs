using Dapper;
using FxOs.Api.Tenancy;
using FxOs.Platform.Audit;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Npgsql;

namespace FxOs.Api.Endpoints;

/// <summary>
/// The signed-in person's profile and notifications (U-01, U-03), company info and banks (O-03, O-04, C-01) and the
/// onboarding steps. Company changes are for the company's admins only; every change is in the company's audit log.
/// </summary>
public static class CompanyEndpoints
{
    private static readonly string[] Directions = ["usd_to_mxn", "mxn_to_usd", "both"];
    private static readonly string[] Currencies = ["MXN", "USD"];

    public static RouteGroupBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant").WithTags("Company");

        // ── Me (U-01) ────────────────────────────────────────────
        group.MapGet("/me", async (ITenantConnectionFactory db, ITenantContext tenant, AccountService accounts, CancellationToken ct) =>
        {
            var who = tenant.Principal;
            if (who is null)
            {
                return Results.Json(new { code = "sign_in_required" }, statusCode: StatusCodes.Status403Forbidden);
            }

            await using var conn = await db.OpenAsync(ct);
            var p = await conn.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
                "select display_name, language, number_format, time_zone from user_profile where user_id = @id",
                new { id = who.UserId }, cancellationToken: ct));
            return Results.Ok(new
            {
                displayName = p?.DisplayName ?? who.DisplayName,
                email = who.Email,
                role = who.Role,
                language = p?.Language ?? "es-MX",
                numberFormat = p?.NumberFormat ?? "period_decimal",
                timeZone = p?.TimeZone ?? "America/Mexico_City",
                passwordChangedAt = await accounts.PasswordChangedAtAsync(who.UserId, ct),
            });
        });

        group.MapPut("/me", async (ProfileBody body, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit,
            AccountService accounts, CancellationToken ct) =>
        {
            var who = tenant.Principal;
            if (who is null)
            {
                return Results.Json(new { code = "sign_in_required" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var name = (body.DisplayName ?? "").Trim();
            var errors = new Dictionary<string, string[]>();
            if (name.Length is 0 or > 200) errors["displayName"] = ["A name is required."];
            if (body.Language is not ("es-MX" or "en-US")) errors["language"] = ["es-MX or en-US."];
            if (body.NumberFormat is not ("period_decimal" or "comma_decimal")) errors["numberFormat"] = ["period_decimal or comma_decimal."];
            if (!ValidTimeZone(body.TimeZone)) errors["timeZone"] = ["An IANA time zone, e.g. America/Mexico_City."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            await using (var conn = await db.OpenAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into user_profile (user_id, email, display_name, language, number_format, time_zone)
                    values (@id, @email, @name, @language, @numberFormat, @timeZone)
                    on conflict (user_id) do update
                        set display_name = excluded.display_name, language = excluded.language,
                            number_format = excluded.number_format, time_zone = excluded.time_zone, updated_at = now()
                    """,
                    new { id = who.UserId, email = who.Email, name, language = body.Language, numberFormat = body.NumberFormat, timeZone = body.TimeZone },
                    tx, cancellationToken: ct));
                await audit.WriteAsync(conn, tx, new AuditEntry("profile.updated", "user", who.UserId.ToString(),
                    new { body.Language, body.NumberFormat, body.TimeZone }), ct);
                await tx.CommitAsync(ct);
            }

            if (name != who.DisplayName)
            {
                await accounts.UpdateNameAsync(who.UserId, name, ct);
            }

            return Results.NoContent();
        });

        // ── Notifications (U-03): defaults live in notification_type; rows only for changes. Items marked
        //    approvers_only (approval requests) are shown to approvers and admins only. ──
        group.MapGet("/me/notifications", async (ITenantConnectionFactory db, ITenantContext tenant, CancellationToken ct) =>
        {
            var who = tenant.Principal;
            if (who is null) return Results.Json(new { code = "sign_in_required" }, statusCode: StatusCodes.Status403Forbidden);
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<NotificationRow>(new CommandDefinition(
                """
                select t.code, t.default_enabled, t.approvers_only, coalesce(p.email_enabled, t.default_enabled) as enabled
                from notification_type t
                left join notification_preference p on p.notification_code = t.code and p.user_id = @id
                where not t.approvers_only or @canApprove
                order by t.sort_order
                """, new { id = who.UserId, canApprove = CanApprove(who) }, cancellationToken: ct));
            return Results.Ok(rows);
        });

        group.MapPut("/me/notifications", async (NotificationsBody body, ITenantConnectionFactory db, ITenantContext tenant,
            IAuditWriter audit, CancellationToken ct) =>
        {
            var who = tenant.Principal;
            if (who is null) return Results.Json(new { code = "sign_in_required" }, statusCode: StatusCodes.Status403Forbidden);
            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            // Profile row first (preferences reference it).
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into user_profile (user_id, email, display_name) values (@id, @email, @name)
                on conflict (user_id) do nothing
                """, new { id = who.UserId, email = who.Email, name = who.DisplayName }, tx, cancellationToken: ct));
            foreach (var item in body.Items ?? [])
            {
                var isDefault = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                    "select default_enabled = @enabled from notification_type where code = @code and (not approvers_only or @canApprove)",
                    new { code = item.Code, enabled = item.Enabled, canApprove = CanApprove(who) }, tx, cancellationToken: ct));
                if (isDefault is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [$"Unknown notification '{item.Code}'."] });
                }

                await conn.ExecuteAsync(new CommandDefinition(isDefault.Value
                    ? "delete from notification_preference where user_id = @id and notification_code = @code"
                    : """
                      insert into notification_preference (user_id, notification_code, email_enabled) values (@id, @code, @enabled)
                      on conflict (user_id, notification_code) do update set email_enabled = excluded.email_enabled, updated_at = now()
                      """,
                    new { id = who.UserId, code = item.Code, enabled = item.Enabled }, tx, cancellationToken: ct));
            }

            await audit.WriteAsync(conn, tx, new AuditEntry("notifications.updated", "user", who.UserId.ToString(),
                new { items = body.Items }), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // ── Company info and banks (O-03, O-04, C-01) ──────────────
        group.MapGet("/company", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await CompanyAsync(conn, null, ct));
        });

        group.MapPut("/company", async (CompanyBody body, ITenantConnectionFactory db, ITenantContext tenant, IAuditWriter audit,
            ControlPlaneDb controlPlane, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = (body.DisplayName ?? "").Trim();
            if (name.Length is 0 or > 200) errors["displayName"] = ["A company name is required."];
            if (body.LegalName is { Length: > 200 }) errors["legalName"] = ["At most 200 characters."];
            if (body.Country is null || !System.Text.RegularExpressions.Regex.IsMatch(body.Country, "^[A-Z]{2}$")) errors["country"] = ["A two-letter country code."];
            if (!ValidTimeZone(body.TimeZone)) errors["timeZone"] = ["An IANA time zone."];
            if (!Directions.Contains(body.ConversionDirection)) errors["conversionDirection"] = ["usd_to_mxn, mxn_to_usd or both."];
            if (body.Locale is not ("es-MX" or "en-US")) errors["locale"] = ["es-MX or en-US."];
            if (!Currencies.Contains(body.BaseCurrency)) errors["baseCurrency"] = ["MXN or USD."];
            if (!Currencies.Contains(body.ReportingCurrency)) errors["reportingCurrency"] = ["MXN or USD."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var legal = string.IsNullOrWhiteSpace(body.LegalName) ? null : body.LegalName.Trim();
            await using (var conn = await db.OpenAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    update tenant_settings
                    set display_name = @name, legal_name = @legal, country = @country, time_zone = @timeZone,
                        conversion_direction = @direction, locale = @locale, base_currency = @baseCurrency,
                        reporting_currency = @reportingCurrency,
                        company_confirmed_at = case when @confirm then coalesce(company_confirmed_at, now()) else company_confirmed_at end,
                        updated_at = now()
                    where id = 1
                    """,
                    new
                    {
                        name, legal, country = body.Country, timeZone = body.TimeZone, direction = body.ConversionDirection,
                        locale = body.Locale, baseCurrency = body.BaseCurrency, reportingCurrency = body.ReportingCurrency,
                        confirm = body.ConfirmOnboardingStep,
                    }, tx, cancellationToken: ct));
                await audit.WriteAsync(conn, tx, new AuditEntry("company.updated", "tenant", tenant.Require().Id.ToString(),
                    new { name, body.Country, body.TimeZone, body.ConversionDirection, body.Locale, body.BaseCurrency, body.ReportingCurrency }), ct);
                await tx.CommitAsync(ct);
            }

            // The company name is also the tenant's name in the menu and the portal.
            await using (var cp = await controlPlane.OpenAsync(ct))
            {
                await cp.ExecuteAsync(new CommandDefinition(
                    "update tenant set display_name = @name, updated_at = now() where id = @id and display_name <> @name",
                    new { name, id = tenant.Require().Id }, cancellationToken: ct));
            }

            return Results.NoContent();
        }).CompanyAdminOnly();

        group.MapPut("/banks", async (BanksBody body, ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var wanted = (body.Banks ?? [])
                .Select(b => b with { Name = (b.Name ?? "").Trim() })
                .Where(b => b.Name.Length > 0)
                .ToList();
            if (wanted.Any(b => !Currencies.Contains(b.Currency) || b.Name.Length > 120))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["banks"] = ["Each bank needs a name (max 120) and MXN or USD."] });
            }

            // Duplicate names within a currency are ignored (case-insensitive); the first one wins.
            wanted = wanted.GroupBy(b => (b.Currency, b.Name.ToLowerInvariant())).Select(g => g.First()).ToList();

            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var current = (await conn.QueryAsync<(Guid Id, string Name, string Currency)>(new CommandDefinition(
                    "select id, name, trim(currency) from bank where removed_at is null for update", transaction: tx, cancellationToken: ct))).ToList();

                // A bank sent without its id is the existing one of the same currency and name (ignoring case).
                wanted = wanted.Select(b => b.Id is not null ? b : b with
                {
                    Id = current.Where(c => c.Currency == b.Currency && string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(c => (Guid?)c.Id).FirstOrDefault(),
                }).ToList();
                var keep = wanted.Where(b => b.Id is { } id && current.Any(c => c.Id == id)).ToList();
                var removed = current.Where(c => !keep.Any(k => k.Id == c.Id)).ToList();
                var added = wanted.Where(b => b.Id is null || !current.Any(c => c.Id == b.Id)).ToList();
                var renamed = keep.Where(k => current.First(c => c.Id == k.Id).Name != k.Name).ToList();

                // Add first, then rename, then remove: the "last bank of a currency" rule sees the final list.
                foreach (var b in added)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "insert into bank (name, currency) values (@name, @currency)", new { name = b.Name, currency = b.Currency }, tx, cancellationToken: ct));
                }

                foreach (var b in renamed)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "update bank set name = @name, updated_at = now() where id = @id", new { name = b.Name, id = b.Id }, tx, cancellationToken: ct));
                }

                foreach (var b in removed)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "update bank set removed_at = now(), updated_at = now() where id = @id", new { id = b.Id }, tx, cancellationToken: ct));
                }

                await audit.WriteAsync(conn, tx, new AuditEntry("banks.updated", "bank", null, new
                {
                    added = added.Select(b => new { b.Name, b.Currency }),
                    renamed = renamed.Select(b => new { b.Id, b.Name }),
                    removed = removed.Select(b => new { b.Id, b.Name, b.Currency }),
                }), ct);
                await tx.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return Results.Json(new { code = "last_bank", message = "Keep at least one MXN bank and one USD bank." },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Json(new { code = "duplicate", message = "That bank name is already in use for that currency." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var read = await db.OpenAsync(ct);
            return Results.Ok(await CompanyAsync(read, null, ct));
        }).CompanyAdminOnly();

        // O-04 → O-05: finishing needs a bank of each currency (O-S2); the database checks it too.
        group.MapPost("/onboarding/complete", async (OnboardingBody body, ITenantConnectionFactory db, ITenantContext tenant,
            IAuditWriter audit, ControlPlaneDb controlPlane, EmailOutbox outbox, AppLinks links, CancellationToken ct) =>
        {
            await using (var conn = await db.OpenAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                try
                {
                    var changed = await conn.ExecuteAsync(new CommandDefinition(
                        "update tenant_settings set onboarding_completed_at = now(), updated_at = now() where id = 1 and onboarding_completed_at is null",
                        transaction: tx, cancellationToken: ct));
                    if (changed == 0)
                    {
                        return Results.NoContent();
                    }
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
                {
                    return Results.Json(new { code = ex.ConstraintName == "tenant_settings_onboarding_after_company" ? "company_first" : "banks_missing" },
                        statusCode: StatusCodes.Status409Conflict);
                }

                await audit.WriteAsync(conn, tx, new AuditEntry("onboarding.completed", "tenant", tenant.Require().Id.ToString()), ct);
                await tx.CommitAsync(ct);
            }

            // Welcome email to the admin who finished setup.
            var who = tenant.Principal!;
            await using var cp = await controlPlane.OpenAsync(ct);
            await outbox.EnqueueAsync(cp, null, EmailTemplates.Welcome(body.Locale is "en-US" ? "en-US" : "es-MX", who.Email,
                new WelcomeModel(tenant.Require().Id, who.DisplayName, tenant.Require().DisplayName, links.Dashboard)), ct);
            return Results.NoContent();
        }).CompanyAdminOnly();

        return group;
    }

    private static bool CanApprove(SessionPrincipal who) => who.Role is MembershipRole.Approver or MembershipRole.Admin;

    private static async Task<object> CompanyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var s = await conn.QuerySingleAsync<CompanyRow>(new CommandDefinition(
            """
            select display_name, legal_name, country, time_zone, conversion_direction, locale, base_currency, reporting_currency,
                   company_confirmed_at, onboarding_completed_at
            from tenant_settings where id = 1
            """, transaction: tx, cancellationToken: ct));
        var banks = await conn.QueryAsync<BankRow>(new CommandDefinition(
            "select id, name, currency from bank where removed_at is null order by currency, created_at", transaction: tx, cancellationToken: ct));
        return new
        {
            s.DisplayName, s.LegalName, country = s.Country.Trim(), s.TimeZone, s.ConversionDirection, s.Locale,
            baseCurrency = s.BaseCurrency.Trim(), reportingCurrency = s.ReportingCurrency.Trim(),
            onboarding = new { companyConfirmed = s.CompanyConfirmedAt is not null, completed = s.OnboardingCompletedAt is not null },
            banks = banks.Select(b => new { b.Id, b.Name, currency = b.Currency.Trim() }),
        };
    }

    private static bool ValidTimeZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 64)
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zone);
            return zone.Contains('/', StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private sealed class ProfileRow
    {
        public string DisplayName { get; set; } = "";
        public string Language { get; set; } = "";
        public string NumberFormat { get; set; } = "";
        public string TimeZone { get; set; } = "";
    }

    private sealed class NotificationRow
    {
        public string Code { get; set; } = "";
        public bool DefaultEnabled { get; set; }
        public bool ApproversOnly { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class CompanyRow
    {
        public string DisplayName { get; set; } = "";
        public string? LegalName { get; set; }
        public string Country { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public string ConversionDirection { get; set; } = "";
        public string Locale { get; set; } = "";
        public string BaseCurrency { get; set; } = "";
        public string ReportingCurrency { get; set; } = "";
        public DateTime? CompanyConfirmedAt { get; set; }
        public DateTime? OnboardingCompletedAt { get; set; }
    }

    private sealed class BankRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Currency { get; set; } = "";
    }
}

public sealed record ProfileBody(string? DisplayName, string? Language, string? NumberFormat, string? TimeZone);

public sealed record NotificationItem(string Code, bool Enabled);

public sealed record NotificationsBody(List<NotificationItem>? Items);

public sealed record CompanyBody(
    string? DisplayName, string? LegalName, string? Country, string? TimeZone, string? ConversionDirection, string? Locale,
    string? BaseCurrency, string? ReportingCurrency, bool ConfirmOnboardingStep);

public sealed record BankItem(Guid? Id, string? Name, string Currency);

public sealed record BanksBody(List<BankItem>? Banks);

public sealed record OnboardingBody(string? Locale);
