using Dapper;
using FxOs.Platform.Audit;
using FxOs.Platform.Tenancy;
using Npgsql;

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
                """
                select (select count(*) from legal_entity) as legal_entities,
                       (select count(*) from department)   as departments,
                       (select count(*) from user_profile) as users
                """,
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

        group.MapGet("/legal-entities", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<LegalEntityRow>(new CommandDefinition(
                "select id, name, tax_id, country, functional_currency from legal_entity order by name",
                cancellationToken: ct));
            return Results.Ok(rows);
        });

        group.MapGet("/departments", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<DepartmentRow>(new CommandDefinition(
                """
                select d.id, d.name, d.legal_entity_id, le.name as legal_entity_name
                from department d
                left join legal_entity le on le.id = d.legal_entity_id
                order by le.name nulls first, d.name
                """,
                cancellationToken: ct));
            return Results.Ok(rows);
        });

        group.MapPost("/departments", async (
            CreateDepartmentRequest body, ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 120)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Name is required (max 120 characters)."],
                });
            }

            await using var conn = await db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                    "insert into department (legal_entity_id, name) values (@legalEntityId, @name) returning id",
                    new { legalEntityId = body.LegalEntityId, name },
                    tx,
                    cancellationToken: ct));
                await audit.WriteAsync(conn, tx,
                    new AuditEntry("department.created", "department", id.ToString(), new { name, body.LegalEntityId }), ct);
                await tx.CommitAsync(ct);
                return Results.Created($"/api/tenant/departments/{id}", new { id, name, legalEntityId = body.LegalEntityId });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Department exists",
                    detail: $"A department named '{name}' already exists for that legal entity.");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["legalEntityId"] = ["Legal entity not found."],
                });
            }
        });

        group.MapGet("/users", async (ITenantConnectionFactory db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
                """
                select u.id, u.email, u.display_name, u.role, d.name as department_name
                from user_profile u
                left join department d on d.id = u.department_id
                order by u.display_name
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

public sealed record CreateDepartmentRequest(string? Name, Guid? LegalEntityId);

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
    public long LegalEntities { get; set; }
    public long Departments { get; set; }
    public long Users { get; set; }
}

internal sealed class LegalEntityRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? TaxId { get; set; }
    public string Country { get; set; } = "";
    public string FunctionalCurrency { get; set; } = "";
}

internal sealed class DepartmentRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? LegalEntityId { get; set; }
    public string? LegalEntityName { get; set; }
}

internal sealed class UserRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string? DepartmentName { get; set; }
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
