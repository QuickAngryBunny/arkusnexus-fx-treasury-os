using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxOs.Platform.Tenancy;

/// <summary>
/// Runs the API's part of tenant setup (A-03): the database step (create, migrate, route — the tenant stays
/// 'provisioning'), then, after the Python worker has copied the rate history, the invitation step: the setup
/// invitation and its email go out in one transaction and the tenant becomes active. Every step is idempotent, so a
/// retry after a failure continues where it stopped.
/// </summary>
public sealed class TenantSetupRunner(
    ControlPlaneDb controlPlane, ControlPlaneStore store, TenantProvisioner provisioner, IdentityStore identity,
    EmailOutbox outbox, AppLinks links, ILogger<TenantSetupRunner> log)
{
    /// <summary>Runs every due database and invitation step once. Returns how many steps ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var ran = 0;
        while (await ClaimAsync(TenantJobStep.Database, ct) is { } job)
        {
            await DatabaseStepAsync(job, ct);
            ran++;
        }

        while (await ClaimAsync(TenantJobStep.Invitation, ct) is { } job)
        {
            await InvitationStepAsync(job, ct);
            ran++;
        }

        return ran;
    }

    /// <summary>After a restart, steps this process was running are picked up again (they are idempotent).</summary>
    public async Task ResumeInterruptedAsync(CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            """
            update tenant_job set status = 'pending', updated_at = now()
            where kind = 'setup' and step in ('database', 'invitation') and status = 'running'
            """, ct: ct);
    }

    private async Task DatabaseStepAsync(ClaimedJob job, CancellationToken ct)
    {
        try
        {
            var tenant = await store.FindByIdAsync(job.TenantId, ct)
                ?? throw new InvalidOperationException("The tenant no longer exists.");
            var ready = await provisioner.ProvisionAsync(tenant.Slug, tenant.DisplayName, tenant.IsDummy, activate: false, ct);
            await AdvanceAsync(job.Id, TenantJobStep.Database, TenantJobStep.RateHistory, $"schema v{ready.SchemaVersion}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Setup of tenant {TenantId}: database step failed", job.TenantId);
            await FailAsync(job.Id, "Creating the database failed: " + ex.Message, ct);
        }
    }

    private async Task InvitationStepAsync(ClaimedJob job, CancellationToken ct)
    {
        try
        {
            var tenant = await store.FindByIdAsync(job.TenantId, ct)
                ?? throw new InvalidOperationException("The tenant no longer exists.");
            var email = job.FirstAdminEmail!;
            var name = NameFrom(email);

            await using var conn = await controlPlane.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var actor = job.RequestedBy is { } by ? ControlActor.PlatformAdmin(by) : ControlActor.System;
            var issued = await identity.IssueInvitationAsync(conn, tx,
                new NewInvitation(tenant.Id, InvitationKind.TenantSetup, email, name, MembershipRole.Admin), actor, ct);
            await outbox.EnqueueAsync(conn, tx, EmailTemplates.TenantSetupInvitation("es-MX", email,
                new TenantSetupInvitationModel(tenant.Id, name, tenant.DisplayName, links.TenantSetup(issued.RawToken),
                    issued.ExpiresAt, EmailClock.MexicoCity)), ct); // the company has not chosen a time zone yet (O-03)
            await conn.ExecAsync(
                "update tenant set status = 'active', updated_at = now() where id = @id and status in ('provisioning', 'failed')",
                new { id = tenant.Id }, tx, ct);
            await conn.ExecAsync(
                """
                update tenant_job
                set step = 'done', status = 'done', progress = 100, finished_at = now(), updated_at = now(),
                    events = events || jsonb_build_array(jsonb_build_object('step', 'invitation', 'at', now(), 'note', @email))
                where id = @id
                """,
                new { id = job.Id, email }, tx, ct);
            await ControlAudit.WriteAsync(conn, tx, ControlActor.System, "tenant.setup_completed", tenant.Id, "tenant",
                tenant.Id.ToString(), ct: ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Setup of tenant {TenantId}: invitation step failed", job.TenantId);
            await FailAsync(job.Id, "Sending the setup invitation failed: " + ex.Message, ct);
        }
    }

    private async Task<ClaimedJob?> ClaimAsync(string step, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClaimedJob>(new CommandDefinition(
            """
            update tenant_job set status = 'running', updated_at = now()
            where id = (select id from tenant_job where kind = 'setup' and step = @step and status = 'pending'
                        order by created_at limit 1 for update skip locked)
            returning id, tenant_id, first_admin_email, requested_by
            """,
            new { step }, cancellationToken: ct));
    }

    private async Task AdvanceAsync(Guid jobId, string done, string next, string note, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            """
            update tenant_job
            set step = @next, status = 'pending', progress = 0, updated_at = now(),
                events = events || jsonb_build_array(jsonb_build_object('step', @done, 'at', now(), 'note', @note))
            where id = @jobId
            """,
            new { jobId, done, next, note }, ct: ct);
    }

    private async Task FailAsync(Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            "update tenant_job set status = 'failed', error = @error, updated_at = now() where id = @jobId",
            new { jobId, error = error.Length > 1000 ? error[..1000] : error }, ct: ct);
    }

    /// <summary>"ana.ruiz@x" → "Ana Ruiz"; the person confirms their real name in onboarding (O-02).</summary>
    internal static string NameFrom(string email)
    {
        var local = email.Split('@')[0];
        var words = local.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length > 0 ? name : email;
    }

    private sealed class ClaimedJob
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string? FirstAdminEmail { get; set; }
        public Guid? RequestedBy { get; set; }
    }
}

/// <summary>Polls for setup work every few seconds.</summary>
public sealed class TenantSetupService(TenantSetupRunner runner, ILogger<TenantSetupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runner.ResumeInterruptedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not resume interrupted tenant setups");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Tenant setup pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
