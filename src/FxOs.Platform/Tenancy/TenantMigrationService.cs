using FxOs.Platform.Data;
using FxOs.Platform.Migrations;
using FxOs.Platform.Secrets;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FxOs.Platform.Tenancy;

/// <summary>Brings the control plane and every tenant database to the latest schema (run from CI/CD before each release).</summary>
public sealed class TenantMigrationService(
    ControlPlaneDb controlPlane,
    ControlPlaneStore store,
    TenantServerRegistry servers,
    ISecretStore secrets,
    ILogger<TenantMigrationService> log)
{
    public async Task<int> MigrateControlPlaneAsync(CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await MigrationRunner.RunAsync(conn, MigrationCatalog.ControlPlane, log, ct);
    }

    /// <returns>Number of tenant databases that failed to migrate.</returns>
    public async Task<int> MigrateAllTenantsAsync(CancellationToken ct = default)
    {
        var failures = 0;
        foreach (var tenant in await store.ListAsync(ct))
        {
            if (tenant.DatabaseName is null || tenant.LoginRole is null || tenant.SecretRef is null || tenant.ServerKey is null)
            {
                log.LogWarning("Skipping tenant {Slug}: no database routing (status {Status})", tenant.Slug, tenant.Status);
                continue;
            }

            try
            {
                var password = await secrets.GetAsync(tenant.SecretRef, ct)
                    ?? throw new InvalidOperationException($"Secret '{tenant.SecretRef}' not found.");
                await using var conn = new NpgsqlConnection(
                    servers.TenantConnectionString(tenant.ServerKey, tenant.DatabaseName, tenant.LoginRole, password));
                await conn.OpenAsync(ct);
                var version = await MigrationRunner.RunAsync(conn, MigrationCatalog.Tenant, log, ct);

                await using var cp = await controlPlane.OpenAsync(ct);
                await cp.ExecAsync(
                    "update tenant_database set schema_version = @version, migrated_at = now() where tenant_id = @id",
                    new { version, id = tenant.Id },
                    ct: ct);
                log.LogInformation("Tenant {Slug} at schema v{Version}", tenant.Slug, version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                log.LogError(ex, "Migrating tenant {Slug} failed", tenant.Slug);
            }
        }

        return failures;
    }
}
