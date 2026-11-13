using Dapper;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>Tenant-scoped endpoints. Every route here runs behind TenantResolutionMiddleware.</summary>
public static class TenantEndpoints
{
    public static RouteGroupBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant").WithTags("Tenant");

        group.MapGet("/profile", async (ITenantConnectionFactory db, ITenantContext tenant, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var settings = await conn.QuerySingleAsync<TenantSettingsRow>(new CommandDefinition(
                "select tenant_id, display_name, base_currency, locale, time_zone from tenant_settings where id = 1",
                cancellationToken: ct));
            var counts = await conn.QuerySingleAsync<TenantCountsRow>(new CommandDefinition(
                "select count(*) as users from user_profile",
                cancellationToken: ct));

            return Results.Ok(new
            {
                slug = tenant.Require().Slug,
                settings.TenantId,
                settings.DisplayName,
                settings.BaseCurrency,
                settings.Locale,
                settings.TimeZone,
                counts,
            });
        });

        group.MapGet("/users", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
                """
                select id, email, display_name, role
                from user_profile
                order by display_name
                """,
                cancellationToken: ct));
            return Results.Ok(rows);
        });

        group.MapGet("/audit", async (ITenantConnectionFactory db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition(
                """
                select id, occurred_at, actor, action, entity_type, entity_id, data::text as data
                from audit_log order by id desc limit @take
                """,
                new { take },
                cancellationToken: ct));
            return Results.Ok(rows);
        });

        return group;
    }
}

internal sealed class TenantSettingsRow
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = "";
    public string BaseCurrency { get; set; } = "";
    public string Locale { get; set; } = "";
    public string TimeZone { get; set; } = "";
}

internal sealed class TenantCountsRow
{
    public long Users { get; set; }
}

internal sealed class UserRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
}

internal sealed class AuditRow
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string Data { get; set; } = "{}";
}
