using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.Logging;

namespace FxOs.Platform.Email;

/// <summary>
/// The time zone an email shows its times in (U-01 "time zone used for email times"): the recipient's own profile
/// setting in the company, else the company's time zone once onboarding confirmed it, else Mexico City. Reading it never
/// blocks an email: any failure falls back to Mexico City.
/// </summary>
public sealed class EmailClocks(ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, ILogger<EmailClocks> log)
{
    /// <summary>
    /// For a person: their profile in <paramref name="tenantId"/>, or (none given) in the company they used last, else
    /// their oldest active membership.
    /// </summary>
    public async Task<EmailClock> ForUserAsync(Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        try
        {
            tenantId ??= await LastCompanyAsync(userId, ct);
            return tenantId is { } id
                ? await ReadAsync(id, "select time_zone from user_profile where user_id = @userId", new { userId }, ct)
                    ?? await CompanyZoneAsync(id, ct)
                    ?? EmailClock.MexicoCity
                : EmailClock.MexicoCity;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Could not read the email time zone for a user; using Mexico City.");
            return EmailClock.MexicoCity;
        }
    }

    /// <summary>For someone without a profile yet (invitations): the company's time zone once confirmed in onboarding.</summary>
    public async Task<EmailClock> ForCompanyAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            return await CompanyZoneAsync(tenantId, ct) ?? EmailClock.MexicoCity;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Could not read the company time zone for an email; using Mexico City.");
            return EmailClock.MexicoCity;
        }
    }

    // Before onboarding step 2 the company's zone is only the default, not a choice.
    private Task<EmailClock?> CompanyZoneAsync(Guid tenantId, CancellationToken ct) =>
        ReadAsync(tenantId, "select time_zone from tenant_settings where id = 1 and company_confirmed_at is not null", null, ct);

    private async Task<Guid?> LastCompanyAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select m.tenant_id
            from tenant_membership m join tenant t on t.id = m.tenant_id and t.status = 'active'
            where m.user_id = @userId and m.status = 'active'
            order by (select max(s.created_at) from session s where s.user_id = m.user_id and s.tenant_id = m.tenant_id) desc nulls last,
                     m.created_at
            limit 1
            """,
            new { userId }, cancellationToken: ct));
    }

    private async Task<EmailClock?> ReadAsync(Guid tenantId, string sql, object? args, CancellationToken ct)
    {
        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant?.DatabaseName is null || tenant.Status == TenantStatus.Deleted)
        {
            return null;
        }

        var source = await tenantDbs.GetAsync(tenant, ct);
        await using var conn = await source.OpenConnectionAsync(ct);
        var zone = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(sql, args, cancellationToken: ct));
        return Valid(zone) ? new EmailClock(zone!) : null;
    }

    private static bool Valid(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zone);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
