using FxOs.Platform.Audit;
using FxOs.Platform.Data;
using FxOs.Platform.Migrations;
using FxOs.Platform.Secrets;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FxOs.Platform.Tenancy;

/// <summary>
/// Creates (or repairs) a tenant: login role, database, schema migrations, settings row and routing.
/// Every step is idempotent, so re-running a failed or partial provisioning is safe.
/// </summary>
public sealed class TenantProvisioner(
    ControlPlaneDb controlPlane,
    ControlPlaneStore store,
    TenantServerRegistry servers,
    ISecretStore secrets,
    ILogger<TenantProvisioner> log)
{
    public const string Actor = "provisioner";

    public async Task<TenantRecord> ProvisionAsync(
        string slug, string displayName, bool isDummy = true, CancellationToken ct = default)
    {
        TenantSlug.EnsureValid(slug);
        displayName = displayName?.Trim() ?? "";
        if (displayName.Length is 0 or > 200)
        {
            throw new ArgumentException("Display name is required (max 200 characters).", nameof(displayName));
        }

        const string serverKey = TenantServerRegistry.DefaultKey;
        var database = TenantSlug.DatabaseName(slug);
        var role = TenantSlug.LoginRole(slug);
        var secretRef = TenantSlug.SecretRef(slug);

        await using var cp = await controlPlane.OpenAsync(ct);
        await cp.ExecAsync("select pg_advisory_lock(hashtext(@key))", new { key = "provision:" + slug }, ct: ct);

        Guid tenantId = Guid.Empty;
        try
        {
            tenantId = await cp.ScalarAsync<Guid>(
                """
                insert into tenant (slug, display_name, status, is_dummy)
                values (@slug, @displayName, 'provisioning', @isDummy)
                on conflict (slug) do update set updated_at = now()
                returning id
                """,
                new { slug, displayName, isDummy },
                ct: ct);

            // 1. Login role, its secret, and the database it owns.
            var password = await secrets.GetAsync(secretRef, ct);
            if (password is null)
            {
                password = Passwords.Generate();
                await secrets.SetAsync(secretRef, password, ct);
            }

            await using (var admin = await servers.OpenAdminAsync(serverKey, "postgres", ct))
            {
                var roleExists = await admin.ScalarAsync<bool>(
                    "select exists (select 1 from pg_roles where rolname = @role)", new { role }, ct: ct);
                var verb = roleExists ? "alter" : "create";
                // Password is alphanumeric (see Passwords) and role is validated, so this literal is safe.
                await admin.ExecAsync(
                    $"{verb} role {Sql.Ident(role)} with login nosuperuser nocreatedb nocreaterole password '{password}'",
                    ct: ct);

                // Managed servers (Azure) need the admin to be a member before it can create a database owned by the role.
                await admin.ExecAsync($"grant {Sql.Ident(role)} to current_user", ct: ct);

                var databaseExists = await admin.ScalarAsync<bool>(
                    "select exists (select 1 from pg_database where datname = @database)", new { database }, ct: ct);
                if (!databaseExists)
                {
                    await admin.ExecAsync($"create database {Sql.Ident(database)} owner {Sql.Ident(role)}", ct: ct);
                }

                // Only this tenant's role may connect. Other tenants' roles get "permission denied".
                await admin.ExecAsync($"revoke connect, temporary on database {Sql.Ident(database)} from public", ct: ct);
                await admin.ExecAsync($"grant connect, temporary on database {Sql.Ident(database)} to {Sql.Ident(role)}", ct: ct);
            }

            await RecordAsync(cp, tenantId, "database", "succeeded", database, ct);

            // 2. Schema and settings, connected as the tenant role itself.
            int version;
            await using (var tenantConn = new NpgsqlConnection(
                servers.TenantConnectionString(serverKey, database, role, password)))
            {
                await tenantConn.OpenAsync(ct);
                version = await MigrationRunner.RunAsync(tenantConn, MigrationCatalog.Tenant, log, ct);

                await tenantConn.ExecAsync(
                    """
                    insert into tenant_settings (id, tenant_id, display_name) values (1, @tenantId, @displayName)
                    on conflict (id) do update
                        set display_name = excluded.display_name, updated_at = now()
                        where tenant_settings.tenant_id = excluded.tenant_id
                    """,
                    new { tenantId, displayName },
                    ct: ct);

                var owner = await tenantConn.ScalarAsync<Guid>("select tenant_id from tenant_settings where id = 1", ct: ct);
                if (owner != tenantId)
                {
                    throw new TenantIsolationException(
                        $"Database '{database}' already belongs to another tenant ({owner}). Provisioning stopped.");
                }

                await AuditSql.WriteAsync(
                    tenantConn,
                    null,
                    Actor,
                    new AuditEntry("tenant.provisioned", "tenant", tenantId.ToString(), new { slug, schemaVersion = version }),
                    ct);
            }

            await RecordAsync(cp, tenantId, "migrate", "succeeded", $"schema v{version}", ct);

            // 3. Routing, then the tenant goes active.
            await using (var tx = await cp.BeginTransactionAsync(ct))
            {
                await cp.ExecAsync(
                    """
                    insert into tenant_database (tenant_id, server_key, database_name, login_role, secret_ref, schema_version, migrated_at)
                    values (@tenantId, @serverKey, @database, @role, @secretRef, @version, now())
                    on conflict (tenant_id) do update
                        set schema_version = excluded.schema_version, migrated_at = excluded.migrated_at
                    """,
                    new { tenantId, serverKey, database, role, secretRef, version },
                    tx,
                    ct);
                await cp.ExecAsync(
                    "update tenant set status = 'active', updated_at = now() where id = @tenantId",
                    new { tenantId },
                    tx,
                    ct);
                await tx.CommitAsync(ct);
            }

            log.LogInformation("Tenant {Slug} provisioned in {Database} at schema v{Version}", slug, database, version);
            return await store.FindBySlugAsync(slug, ct)
                ?? throw new InvalidOperationException($"Tenant '{slug}' vanished after provisioning.");
        }
        catch (Exception ex) when (tenantId != Guid.Empty)
        {
            log.LogError(ex, "Provisioning tenant {Slug} failed", slug);
            await cp.ExecAsync(
                "update tenant set status = 'failed', updated_at = now() where id = @tenantId and status <> 'active'",
                new { tenantId },
                ct: CancellationToken.None);
            await RecordAsync(cp, tenantId, "provision", "failed", ex.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            await cp.ExecAsync("select pg_advisory_unlock(hashtext(@key))", new { key = "provision:" + slug }, ct: CancellationToken.None);
        }
    }

    private static Task RecordAsync(NpgsqlConnection cp, Guid tenantId, string step, string outcome, string detail, CancellationToken ct) =>
        cp.ExecAsync(
            "insert into provisioning_event (tenant_id, step, outcome, detail) values (@tenantId, @step, @outcome, @detail)",
            new { tenantId, step, outcome, detail },
            ct: ct);
}
