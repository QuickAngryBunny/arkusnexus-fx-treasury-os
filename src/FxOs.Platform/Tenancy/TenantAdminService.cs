using System.Globalization;
using System.Text;
using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Identity;
using FxOs.Platform.Secrets;
using Microsoft.Extensions.Logging;

namespace FxOs.Platform.Tenancy;

public static class TenantJobKind
{
    public const string Setup = "setup";
    public const string LoadDummy = "load_dummy";
    public const string ClearDummy = "clear_dummy";
}

public static class TenantJobStep
{
    public const string Database = "database";
    public const string RateHistory = "rate_history";
    public const string Invitation = "invitation";
    public const string Work = "work";
    public const string Done = "done";
}

public sealed record TenantJobView(
    Guid Id, string Kind, string Step, string Status, int Progress, string? Error, string? FirstAdminEmail,
    string EventsJson, DateTime CreatedAt, DateTime? FinishedAt);

public sealed record TenantSummary(
    Guid Id, string Slug, string DisplayName, string Status, bool IsDummy, DateTime CreatedAt, int? SchemaVersion,
    TenantJobView? Setup, TenantJobView? DummyJob, int Members);

public sealed record AdminResult(bool Ok, string? Code = null, string? Message = null, Guid? TenantId = null);

/// <summary>
/// The tenant admin portal's operations (A-01 to A-04, A-S2, A-S3). Every change is written to control_audit_log as
/// the platform admin who made it. Setup and dummy-data work run in the background (TenantSetupRunner in the API for
/// database and invitation steps; the Python worker for the rate history copy and dummy data).
/// </summary>
public sealed class TenantAdminService(
    ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, TenantServerRegistry servers,
    ISecretStore secrets, ILogger<TenantAdminService> log)
{
    private const string JobColumns =
        "id, kind, step, status, progress, error, first_admin_email, events::text as events_json, created_at, finished_at";

    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var tenants = (await conn.QueryAsync<TenantRow>(new CommandDefinition(
            """
            select t.id, t.slug, t.display_name, t.status, t.is_dummy, t.created_at, d.schema_version,
                   (select count(*) from tenant_membership m where m.tenant_id = t.id and m.status = 'active') as members
            from tenant t left join tenant_database d on d.tenant_id = t.id
            where t.status <> 'deleted'
            order by t.is_dummy, t.display_name
            """, cancellationToken: ct))).AsList();
        var jobs = (await conn.QueryAsync<JobRow>(new CommandDefinition(
            $"""
            select distinct on (tenant_id, kind = 'setup') tenant_id, {JobColumns}
            from tenant_job order by tenant_id, kind = 'setup', created_at desc
            """, cancellationToken: ct))).AsList();

        return tenants.Select(t => new TenantSummary(t.Id, t.Slug, t.DisplayName, t.Status, t.IsDummy, t.CreatedAt, t.SchemaVersion,
                jobs.FirstOrDefault(j => j.TenantId == t.Id && j.Kind == TenantJobKind.Setup)?.ToView(),
                jobs.FirstOrDefault(j => j.TenantId == t.Id && j.Kind != TenantJobKind.Setup)?.ToView(),
                (int)t.Members))
            .ToList();
    }

    public async Task<TenantSummary?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(t => t.Id == id);

    /// <summary>A-02: creates the tenant row and its setup job; the setup runner does the rest.</summary>
    public async Task<AdminResult> CreateAsync(
        string name, string firstAdminEmail, bool isDummy, Guid actorUserId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        var email = IdentityStore.NormalizeEmail(firstAdminEmail ?? "");
        if (name.Length is 0 or > 200)
        {
            return new AdminResult(false, "invalid_name", "A name of 1 to 200 characters is required.");
        }

        if (email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return new AdminResult(false, "invalid_email", "A valid email is required.");
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var slug = await FreeSlugAsync(conn, tx, name, ct);
        var tenantId = await conn.ScalarAsync<Guid>(
            "insert into tenant (slug, display_name, status, is_dummy) values (@slug, @name, 'provisioning', @isDummy) returning id",
            new { slug, name, isDummy }, tx, ct);
        await conn.ExecAsync(
            """
            insert into tenant_job (tenant_id, kind, step, first_admin_email, requested_by)
            values (@tenantId, 'setup', 'database', @email, @actorUserId)
            """,
            new { tenantId, email, actorUserId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.PlatformAdmin(actorUserId), "tenant.created", tenantId, "tenant",
            tenantId.ToString(), new { slug, isDummy, firstAdminEmail = email }, ct);
        await tx.CommitAsync(ct);
        return new AdminResult(true, TenantId: tenantId);
    }

    /// <summary>A-S2: a failed setup continues from the step that failed; nothing done is repeated.</summary>
    public async Task<AdminResult> RetrySetupAsync(Guid tenantId, Guid actorUserId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var retried = await conn.ExecAsync(
            """
            update tenant_job set status = 'pending', error = null, updated_at = now()
            where tenant_id = @tenantId and status = 'failed'
            """,
            new { tenantId }, tx, ct);
        if (retried == 0)
        {
            return new AdminResult(false, "nothing_failed", "No failed work to retry.");
        }

        await ControlAudit.WriteAsync(conn, tx, ControlActor.PlatformAdmin(actorUserId), "tenant.setup_retried", tenantId,
            "tenant", tenantId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
        return new AdminResult(true, TenantId: tenantId);
    }

    public async Task<AdminResult> RenameAsync(Guid tenantId, string name, Guid actorUserId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            return new AdminResult(false, "invalid_name", "A name of 1 to 200 characters is required.");
        }

        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Deleted)
        {
            return new AdminResult(false, "not_found");
        }

        await using (var conn = await controlPlane.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecAsync("update tenant set display_name = @name, updated_at = now() where id = @tenantId",
                new { name, tenantId }, tx, ct);
            await ControlAudit.WriteAsync(conn, tx, ControlActor.PlatformAdmin(actorUserId), "tenant.renamed", tenantId,
                "tenant", tenantId.ToString(), new { from = tenant.DisplayName, to = name }, ct);
            await tx.CommitAsync(ct);
        }

        // The company's own copy of its name (shown in the app and in its audit trail).
        if (tenant.DatabaseName is not null)
        {
            var source = await tenantDbs.GetAsync(tenant, ct);
            await using var tconn = await source.OpenConnectionAsync(ct);
            await tconn.ExecuteAsync(new CommandDefinition(
                "update tenant_settings set display_name = @name, updated_at = now() where id = 1", new { name }, cancellationToken: ct));
        }

        return new AdminResult(true, TenantId: tenantId);
    }

    public Task<AdminResult> ArchiveAsync(Guid tenantId, Guid actorUserId, CancellationToken ct = default) =>
        SetStatusAsync(tenantId, TenantStatus.Active, TenantStatus.Archived, "tenant.archived", actorUserId, ct);

    public Task<AdminResult> RestoreAsync(Guid tenantId, Guid actorUserId, CancellationToken ct = default) =>
        SetStatusAsync(tenantId, TenantStatus.Archived, TenantStatus.Active, "tenant.restored", actorUserId, ct);

    /// <summary>
    /// A-S3: deletes a dummy tenant for good: sessions and open invitations end, memberships are deactivated, the
    /// database, its login and its secret are dropped, and the row stays as 'deleted' for the audit trail. Real-data
    /// tenants can only be archived. The caller must type the tenant's name.
    /// </summary>
    public Task<AdminResult> DeleteAsync(Guid tenantId, string typedName, Guid actorUserId, CancellationToken ct = default) =>
        DeleteAsync(tenantId, typedName, ControlActor.PlatformAdmin(actorUserId), ct);

    /// <summary>Delete a dummy tenant (A-S3); <paramref name="actor"/> is a platform admin, or System from the Migrator.</summary>
    public async Task<AdminResult> DeleteAsync(Guid tenantId, string typedName, ControlActor actor, CancellationToken ct = default)
    {
        var actorUserId = actor.UserId;
        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Deleted)
        {
            return new AdminResult(false, "not_found");
        }

        if (!tenant.IsDummy)
        {
            return new AdminResult(false, "real_data", "Tenants with real data can only be archived.");
        }

        if (!string.Equals((typedName ?? "").Trim(), tenant.DisplayName, StringComparison.Ordinal))
        {
            return new AdminResult(false, "name_mismatch", "Type the tenant name exactly to confirm.");
        }

        await using (var conn = await controlPlane.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecAsync(
                """
                update session set revoked_at = now(), revoked_reason = 'tenant_deleted' where tenant_id = @tenantId and revoked_at is null;
                update invitation set revoked_at = now() where tenant_id = @tenantId and used_at is null and revoked_at is null;
                update tenant_membership set status = 'deactivated', deactivated_at = now(), deactivated_by = @actorUserId, updated_at = now()
                    where tenant_id = @tenantId and status = 'active';
                update tenant_job set status = 'failed', error = 'Tenant deleted.', updated_at = now()
                    where tenant_id = @tenantId and status in ('pending', 'running');
                delete from tenant_database where tenant_id = @tenantId;
                update tenant set status = 'deleted', deleted_at = now(), archived_at = null, updated_at = now() where id = @tenantId;
                """,
                new { tenantId, actorUserId }, tx, ct);
            await ControlAudit.WriteAsync(conn, tx, actor, "tenant.deleted", tenantId,
                "tenant", tenantId.ToString(), new { tenant.Slug, tenant.DisplayName }, ct);
            await tx.CommitAsync(ct);
        }

        // Routing is gone, so nothing can open it any more; drop what's left.
        await tenantDbs.RemoveAsync(tenantId);
        if (tenant.DatabaseName is not null && tenant.LoginRole is not null)
        {
            await using var admin = await servers.OpenAdminAsync(tenant.ServerKey ?? TenantServerRegistry.DefaultKey, "postgres", ct);
            await admin.ExecAsync($"drop database if exists {Sql.Ident(tenant.DatabaseName)} with (force)", ct: ct);
            await admin.ExecAsync($"drop role if exists {Sql.Ident(tenant.LoginRole)}", ct: ct);
        }

        if (tenant.SecretRef is not null)
        {
            await secrets.DeleteAsync(tenant.SecretRef, ct);
        }

        log.LogInformation("Tenant {Slug} deleted", tenant.Slug);
        return new AdminResult(true, TenantId: tenantId);
    }

    /// <summary>
    /// A dummy tenant becomes a real company (e.g. the first one kept after testing). One way: afterwards it can only be
    /// archived, dummy data can no longer be loaded, the dev header and dev-user refuse it, and platform admins need a
    /// membership to open it. Clear its dummy data first; this refuses while seeded dummy users are still there.
    /// </summary>
    public async Task<AdminResult> MarkRealDataAsync(Guid tenantId, ControlActor actor, CancellationToken ct = default)
    {
        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Deleted)
        {
            return new AdminResult(false, "not_found");
        }

        if (!tenant.IsDummy)
        {
            return new AdminResult(false, "already_real", "This tenant already holds real data.");
        }

        if (!tenant.IsActive)
        {
            return new AdminResult(false, "not_active", "Only an active tenant can be marked as real.");
        }

        await using (var tconn = await (await tenantDbs.GetAsync(tenant, ct)).OpenConnectionAsync(ct))
        {
            // Seeded dummy users have no membership in the control plane; real people always do.
            var userIds = (await tconn.QueryAsync<Guid>(new CommandDefinition("select user_id from user_profile", cancellationToken: ct))).ToArray();
            await using var cp = await controlPlane.OpenAsync(ct);
            var members = await cp.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*) from tenant_membership where tenant_id = @tenantId and user_id = any(@userIds)",
                new { tenantId, userIds }, cancellationToken: ct));
            if (members < userIds.Length)
            {
                return new AdminResult(false, "dummy_data", $"Clear the dummy data first: {userIds.Length - members} profile(s) belong to no member.");
            }
        }

        await using (var conn = await controlPlane.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecAsync("update tenant set is_dummy = false, updated_at = now() where id = @tenantId and is_dummy",
                new { tenantId }, tx, ct);
            await ControlAudit.WriteAsync(conn, tx, actor, "tenant.marked_real", tenantId, "tenant", tenantId.ToString(),
                new { tenant.Slug, tenant.DisplayName }, ct);
            await tx.CommitAsync(ct);
        }

        log.LogInformation("Tenant {Slug} marked as holding real data", tenant.Slug);
        return new AdminResult(true, TenantId: tenantId);
    }

    /// <summary>A-04 "Load dummy data" / "Clear dummy data": queued for the Python worker, dummy tenants only.</summary>
    public async Task<AdminResult> RequestDummyDataAsync(Guid tenantId, bool load, Guid actorUserId, CancellationToken ct = default)
    {
        var tenant = await store.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Deleted)
        {
            return new AdminResult(false, "not_found");
        }

        if (!tenant.IsDummy)
        {
            return new AdminResult(false, "real_data", "Dummy data only goes into dummy tenants.");
        }

        if (!tenant.IsActive)
        {
            return new AdminResult(false, "not_active", "Finish setup first.");
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var busy = await conn.ScalarAsync<bool>(
            """
            select exists (select 1 from tenant_job where tenant_id = @tenantId and kind in ('load_dummy', 'clear_dummy')
                           and status in ('pending', 'running', 'failed'))
            """, new { tenantId }, tx, ct);
        if (busy)
        {
            return new AdminResult(false, "busy", "Dummy data work is already queued or failed; retry or wait for it.");
        }

        var kind = load ? TenantJobKind.LoadDummy : TenantJobKind.ClearDummy;
        await conn.ExecAsync(
            "insert into tenant_job (tenant_id, kind, step, requested_by) values (@tenantId, @kind, 'work', @actorUserId)",
            new { tenantId, kind, actorUserId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.PlatformAdmin(actorUserId), $"tenant.{kind}_requested", tenantId,
            "tenant", tenantId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
        return new AdminResult(true, TenantId: tenantId);
    }

    private async Task<AdminResult> SetStatusAsync(
        Guid tenantId, string from, string to, string action, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await conn.ExecAsync(
            """
            update tenant set status = @to, archived_at = case when @to = 'archived' then now() end, updated_at = now()
            where id = @tenantId and status = @from
            """,
            new { tenantId, from, to }, tx, ct);
        if (changed == 0)
        {
            return new AdminResult(false, "wrong_status", $"Only {from} tenants can be {(to == TenantStatus.Archived ? "archived" : "restored")}.");
        }

        if (to == TenantStatus.Archived)
        {
            await conn.ExecAsync(
                "update session set revoked_at = now(), revoked_reason = 'tenant_archived' where tenant_id = @tenantId and revoked_at is null",
                new { tenantId }, tx, ct);
        }

        await ControlAudit.WriteAsync(conn, tx, ControlActor.PlatformAdmin(actorUserId), action, tenantId, "tenant",
            tenantId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
        return new AdminResult(true, TenantId: tenantId);
    }

    /// <summary>"Arkus Nexus" → arkus_nexus; accents dropped; a number is added when the slug is taken (deleted too).</summary>
    public static string SlugFrom(string name)
    {
        var folded = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in folded)
        {
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        }

        var slug = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0 || !char.IsAsciiLetterLower(slug[0]))
        {
            slug = "t_" + slug;
        }

        slug = slug.Length > 26 ? slug[..26].TrimEnd('_') : slug;
        return slug.Length < 3 ? slug + "_co" : slug;
    }

    private static async Task<string> FreeSlugAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, string name, CancellationToken ct)
    {
        var baseSlug = SlugFrom(name);
        for (var n = 1; ; n++)
        {
            var slug = n == 1 ? baseSlug : $"{baseSlug}_{n}";
            if (!await conn.ScalarAsync<bool>("select exists (select 1 from tenant where slug = @slug)", new { slug }, tx, ct))
            {
                return slug;
            }
        }
    }

    private sealed class TenantRow
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsDummy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? SchemaVersion { get; set; }
        public long Members { get; set; }
    }

    private sealed class JobRow
    {
        public Guid TenantId { get; set; }
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string Step { get; set; } = "";
        public string Status { get; set; } = "";
        public short Progress { get; set; }
        public string? Error { get; set; }
        public string? FirstAdminEmail { get; set; }
        public string EventsJson { get; set; } = "[]";
        public DateTime CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public TenantJobView ToView() => new(Id, Kind, Step, Status, Progress, Error, FirstAdminEmail, EventsJson, CreatedAt, FinishedAt);
    }
}
