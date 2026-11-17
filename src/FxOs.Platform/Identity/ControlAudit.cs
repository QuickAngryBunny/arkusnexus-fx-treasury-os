using System.Diagnostics;
using System.Text.Json;
using FxOs.Platform.Data;
using Npgsql;

namespace FxOs.Platform.Identity;

public static class ActorKind
{
    public const string User = "user";
    public const string PlatformAdmin = "platform_admin";
    public const string System = "system";
    public const string Anonymous = "anonymous";
}

/// <summary>Who is acting on the control plane. <see cref="UserId"/> is null for system and anonymous actors.</summary>
public sealed record ControlActor(string Kind, Guid? UserId = null)
{
    public static ControlActor System { get; } = new(ActorKind.System);
    public static ControlActor Anonymous { get; } = new(ActorKind.Anonymous);

    public static ControlActor User(Guid id) => new(ActorKind.User, id);

    public static ControlActor PlatformAdmin(Guid id) => new(ActorKind.PlatformAdmin, id);
}

/// <summary>Writes to the append-only control_audit_log inside the caller's transaction.</summary>
public static class ControlAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, ControlActor actor, string action, Guid? tenantId = null,
        string? entityType = null, string? entityId = null, object? detail = null, CancellationToken ct = default)
    {
        var data = detail is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(detail, Json), Json)!;
        if (Activity.Current is { } activity)
        {
            data["traceId"] = activity.TraceId.ToString();
        }

        return conn.ExecAsync(
            """
            insert into control_audit_log (actor_user_id, actor_kind, tenant_id, action, entity_type, entity_id, detail)
            values (@actorUserId, @actorKind, @tenantId, @action, @entityType, @entityId, cast(@detail as jsonb))
            """,
            new
            {
                actorUserId = actor.UserId,
                actorKind = actor.Kind,
                tenantId,
                action,
                entityType,
                entityId,
                detail = JsonSerializer.Serialize(data, Json),
            },
            tx,
            ct);
    }
}
