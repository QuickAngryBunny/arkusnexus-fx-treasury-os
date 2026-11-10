using System.Diagnostics;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Npgsql;

namespace FxOs.Platform.Audit;

public sealed record AuditEntry(string Action, string? EntityType = null, string? EntityId = null, object? Data = null);

/// <summary>Writes to the tenant's append-only audit_log, inside the caller's transaction when given.</summary>
public interface IAuditWriter
{
    Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, AuditEntry entry, CancellationToken ct = default);
}

public sealed class AuditWriter(ITenantContext tenant) : IAuditWriter
{
    public Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, AuditEntry entry, CancellationToken ct = default)
    {
        tenant.Require();
        return AuditSql.WriteAsync(conn, tx, tenant.Actor, entry, ct);
    }
}

public static class AuditSql
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string actor, AuditEntry entry, CancellationToken ct = default) =>
        conn.ExecAsync(
            """
            insert into audit_log (actor, action, entity_type, entity_id, correlation_id, data)
            values (@actor, @action, @entityType, @entityId, @correlationId, cast(@data as jsonb))
            """,
            new
            {
                actor,
                action = entry.Action,
                entityType = entry.EntityType,
                entityId = entry.EntityId,
                correlationId = Activity.Current?.TraceId.ToString(),
                data = JsonSerializer.Serialize(entry.Data ?? new { }, Json),
            },
            tx,
            ct);
}
