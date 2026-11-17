using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxOs.Platform.Audit;

/// <summary>
/// Copies a company's sign-ins, invitations and membership changes from the control plane's
/// <c>control_audit_log</c> into the company's own <c>audit_log</c> (tenant V0011). The control-plane row is written in
/// the same transaction as the change; this copy follows within seconds. Each row is copied once
/// (<c>audit_log.control_audit_id</c> is unique), and a company that was unreachable catches up on a later pass.
/// Platform operations (tenant.*, dev.*) stay in the control plane only.
/// </summary>
public sealed class ControlAuditMirror(ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, ILogger<ControlAuditMirror> log)
{
    private const string Copied = "action ~ '^(account|invitation|membership)\\.'";
    private const int Batch = 500;
    private long _seen;

    /// <summary>One pass: every company with new entries since the last pass (all of them on the first). Returns rows copied.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenantIds;
        long max;
        await using (var conn = await controlPlane.OpenAsync(ct))
        {
            max = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "select coalesce(max(id), 0) from control_audit_log", cancellationToken: ct));
            tenantIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                $"select distinct tenant_id from control_audit_log where id > @seen and id <= @max and tenant_id is not null and {Copied}",
                new { seen = _seen, max }, cancellationToken: ct))).ToList();
        }

        var copied = 0;
        var allDone = true;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                copied += await CopyAsync(tenantId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                allDone = false;
                log.LogWarning(e, "Could not copy audit entries to tenant {TenantId}; retrying on the next pass", tenantId);
            }
        }

        // Only move on when every company is up to date, so one that failed is looked at again.
        if (allDone)
        {
            _seen = max;
        }

        return copied;
    }

    private async Task<int> CopyAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant?.DatabaseName is null || tenant.Status == TenantStatus.Deleted)
        {
            return 0;
        }

        var source = await tenantDbs.GetAsync(tenant, ct);
        await using var tconn = await source.OpenConnectionAsync(ct);
        var copied = 0;
        while (true)
        {
            var after = await tconn.ExecuteScalarAsync<long>(new CommandDefinition(
                "select coalesce(max(control_audit_id), 0) from audit_log", cancellationToken: ct));

            List<Row> rows;
            await using (var conn = await controlPlane.OpenAsync(ct))
            {
                rows = (await conn.QueryAsync<Row>(new CommandDefinition(
                    $"""
                    select id, occurred_at, actor_kind, actor_user_id, action, entity_type, entity_id, detail::text as detail
                    from control_audit_log
                    where tenant_id = @tenantId and id > @after and {Copied}
                    order by id limit {Batch}
                    """,
                    new { tenantId, after }, cancellationToken: ct))).ToList();
            }

            if (rows.Count == 0)
            {
                return copied;
            }

            await using var tx = await tconn.BeginTransactionAsync(ct);
            foreach (var r in rows)
            {
                copied += await tconn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into audit_log (occurred_at, actor, action, entity_type, entity_id, data, control_audit_id)
                    values (@occurredAt, @actor, @action, @entityType, @entityId,
                            cast(@detail as jsonb) || jsonb_build_object('source', 'control_plane'), @id)
                    on conflict (control_audit_id) do nothing
                    """,
                    new
                    {
                        occurredAt = r.OccurredAt,
                        actor = r.ActorUserId is { } u ? $"{r.ActorKind}:{u:D}" : r.ActorKind,
                        action = r.Action,
                        entityType = r.EntityType,
                        entityId = r.EntityId,
                        detail = r.Detail,
                        id = r.Id,
                    },
                    tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            if (rows.Count < Batch)
            {
                return copied;
            }
        }
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public DateTime OccurredAt { get; set; }
        public string ActorKind { get; set; } = "";
        public Guid? ActorUserId { get; set; }
        public string Action { get; set; } = "";
        public string? EntityType { get; set; }
        public string? EntityId { get; set; }
        public string Detail { get; set; } = "{}";
    }
}

/// <summary>Runs <see cref="ControlAuditMirror"/> every 10 seconds (<c>Platform:MirrorControlAudit</c>, on by default).</summary>
public sealed class ControlAuditMirrorService(ControlAuditMirror mirror, ILogger<ControlAuditMirrorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await mirror.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Audit copy pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
