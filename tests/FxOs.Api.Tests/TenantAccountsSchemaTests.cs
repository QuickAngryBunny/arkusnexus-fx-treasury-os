using Dapper;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Tenant V0009: user profiles and preferences, notification preferences, company info and banks.</summary>
[Collection(PlatformCollection.Name)]
public sealed class TenantAccountsSchemaTests(PlatformFixture fx)
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task Profiles_default_their_preferences_and_reject_unknown_values()
    {
        var tenant = await fx.ProvisionAsync("prof");
        await using var conn = await fx.OpenTenantAsync(tenant);
        var userId = Guid.NewGuid();

        await conn.ExecuteAsync(
            "insert into user_profile (user_id, email, display_name) values (@userId, 'ana@example.test', 'Ana Ruiz')",
            new { userId });
        var prefs = await conn.QuerySingleAsync<(string Language, string NumberFormat, string TimeZone)>(
            "select language, number_format, time_zone from user_profile where user_id = @userId", new { userId });
        Assert.Equal(("es-MX", "period_decimal", "America/Mexico_City"), prefs);

        foreach (var sql in new[]
                 {
                     "update user_profile set language = 'fr-FR'",
                     "update user_profile set number_format = 'space_decimal'",
                     "update user_profile set email = 'Ana@Example.test'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
            Assert.Equal(CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task Notification_types_match_the_brief()
    {
        var tenant = await fx.ProvisionAsync("notif");
        await using var conn = await fx.OpenTenantAsync(tenant);
        var types = (await conn.QueryAsync<(string Code, bool DefaultEnabled, bool ApproversOnly)>(
            "select code, default_enabled, approvers_only from notification_type order by sort_order")).ToList();

        Assert.Equal(9, types.Count);
        Assert.Equal(("weekly_forecast_summary", false, false), types[^1]);
        Assert.Equal(["approval_request"], types.Where(t => t.ApproversOnly).Select(t => t.Code));
        Assert.All(types.Take(8), t => Assert.True(t.DefaultEnabled));

        var userId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "insert into user_profile (user_id, email, display_name) values (@userId, 'leo@example.test', 'Leo')",
            new { userId });
        await conn.ExecuteAsync(
            "insert into notification_preference (user_id, notification_code, email_enabled) values (@userId, 'forecast_move', false)",
            new { userId });
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "insert into notification_preference (user_id, notification_code, email_enabled) values (@userId, 'sms_blast', true)",
            new { userId }));
    }

    [Fact]
    public async Task Company_info_takes_only_mxn_or_usd_and_known_directions()
    {
        var tenant = await fx.ProvisionAsync("company");
        await using var conn = await fx.OpenTenantAsync(tenant);

        await conn.ExecuteAsync("""
            update tenant_settings
            set legal_name = 'Test Company S.A. de C.V.', country = 'MX', conversion_direction = 'both',
                base_currency = 'USD', reporting_currency = 'MXN'
            where id = 1
            """);

        foreach (var sql in new[]
                 {
                     "update tenant_settings set reporting_currency = 'EUR'",
                     "update tenant_settings set conversion_direction = 'sideways'",
                     "update tenant_settings set country = 'mx'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
            Assert.Equal(CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task Bank_names_are_unique_per_currency_ignoring_case_and_removed_names_can_return()
    {
        var tenant = await fx.ProvisionAsync("banks");
        await using var conn = await fx.OpenTenantAsync(tenant);

        var first = await AddBankAsync(conn, "Banco Uno", "MXN");
        var dup = await Assert.ThrowsAsync<PostgresException>(() => AddBankAsync(conn, "banco uno", "MXN"));
        Assert.Equal(UniqueViolation, dup.SqlState);

        // Same name in the other currency is a different bank.
        await AddBankAsync(conn, "Banco Uno", "USD");

        await conn.ExecuteAsync("update bank set removed_at = now() where id = @first", new { first });
        await AddBankAsync(conn, "Banco Uno", "MXN");

        var eur = await Assert.ThrowsAsync<PostgresException>(() => AddBankAsync(conn, "Banco Euro", "EUR"));
        Assert.Equal(CheckViolation, eur.SqlState);
        var padded = await Assert.ThrowsAsync<PostgresException>(() => AddBankAsync(conn, " Banco Dos ", "MXN"));
        Assert.Equal(CheckViolation, padded.SqlState);
    }

    [Fact]
    public async Task Onboarding_needs_a_bank_of_each_currency_and_then_keeps_one()
    {
        var tenant = await fx.ProvisionAsync("onboard");
        await using var conn = await fx.OpenTenantAsync(tenant);
        const string Complete = "update tenant_settings set onboarding_completed_at = now() where id = 1";

        var mxn = await AddBankAsync(conn, "Banco Peso", "MXN");
        var noUsd = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(Complete));
        Assert.Equal(CheckViolation, noUsd.SqlState);

        var usd = await AddBankAsync(conn, "Banco Dólar", "USD");
        await conn.ExecuteAsync(Complete);

        var removeLast = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("update bank set removed_at = now() where id = @mxn", new { mxn }));
        Assert.Equal(CheckViolation, removeLast.SqlState);
        var deleteLast = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("delete from bank where id = @usd", new { usd }));
        Assert.Equal(CheckViolation, deleteLast.SqlState);

        // With a second peso bank, the first one can go; a rename never trips the rule.
        await AddBankAsync(conn, "Banco Peso Dos", "MXN");
        await conn.ExecuteAsync("update bank set removed_at = now() where id = @mxn", new { mxn });
        await conn.ExecuteAsync("update bank set name = 'Banco Dólar Nuevo' where id = @usd", new { usd });
    }

    [Fact]
    public async Task Banks_and_profiles_stay_in_their_own_tenant()
    {
        var a = await fx.ProvisionAsync("xbank");
        var b = await fx.ProvisionAsync("xbank");

        await using (var connA = await fx.OpenTenantAsync(a))
        {
            await AddBankAsync(connA, "Solo en A", "MXN");
            await connA.ExecuteAsync(
                "insert into user_profile (user_id, email, display_name) values (@id, 'only.a@example.test', 'Only A')",
                new { id = Guid.NewGuid() });
        }

        await using var connB = await fx.OpenTenantAsync(b);
        Assert.Equal(0L, await connB.ExecuteScalarAsync<long>("select count(*) from bank"));
        Assert.Equal(0L, await connB.ExecuteScalarAsync<long>(
            "select count(*) from user_profile where email = 'only.a@example.test'"));
    }

    private static Task<Guid> AddBankAsync(NpgsqlConnection conn, string name, string currency) =>
        conn.ExecuteScalarAsync<Guid>(
            "insert into bank (name, currency) values (@name, @currency) returning id", new { name, currency });
}
