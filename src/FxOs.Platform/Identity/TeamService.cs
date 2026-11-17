using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using Npgsql;

namespace FxOs.Platform.Identity;

public sealed record TeamMember(
    Guid UserId, string DisplayName, string Email, string Role, string Status, DateTimeOffset? LastSignInAt);

public sealed record PendingInvitation(
    Guid Id, string DisplayName, string Email, string Role, string State, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public sealed record TeamView(IReadOnlyList<TeamMember> Members, IReadOnlyList<PendingInvitation> Invitations);

public sealed record TeamResult(bool Ok, string? Code = null, string? Message = null);

/// <summary>
/// Settings › Team for company admins (T-01 to T-03, T-S3): people and open invitations, invite (Standard or
/// Approver), resend, cancel, change role, deactivate and reactivate. Every change is audited in the control plane and
/// its email goes to the outbox in the same transaction. A company always keeps at least one active admin.
/// </summary>
public sealed class TeamService(
    ControlPlaneDb controlPlane, IdentityStore identity, InvitationFlow invitations, EmailOutbox outbox, AppLinks links, TimeProvider time)
{
    public async Task<TeamView> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var members = (await conn.QueryAsync<MemberRow>(new CommandDefinition(
            """
            select u.id as user_id, u.display_name, u.email, m.role, m.status, u.last_sign_in_at
            from tenant_membership m join user_account u on u.id = m.user_id
            where m.tenant_id = @tenantId
            order by m.status, u.display_name
            """, new { tenantId }, cancellationToken: ct)))
            .Select(r => new TeamMember(r.UserId, r.DisplayName, r.Email, r.Role, r.Status, Utc(r.LastSignInAt)))
            .ToList();

        var now = time.GetUtcNow();
        var open = (await conn.QueryAsync<InvitationRow>(new CommandDefinition(
            """
            select id, display_name, email, role, expires_at, created_at from invitation
            where tenant_id = @tenantId and kind = 'teammate' and used_at is null and revoked_at is null
            order by created_at desc
            """, new { tenantId }, cancellationToken: ct)))
            .Select(r => new PendingInvitation(r.Id, r.DisplayName, r.Email, r.Role,
                Utc(r.ExpiresAt)!.Value <= now ? "expired" : "pending", Utc(r.ExpiresAt)!.Value, Utc(r.CreatedAt)!.Value))
            .ToList();
        return new TeamView(members, open);
    }

    /// <summary>T-02: invite a teammate as Standard or Approver. T-S3 when the email is already in the company.</summary>
    public async Task<TeamResult> InviteAsync(
        Guid tenantId, string company, Guid inviterId, string displayName, string email, string role, string? note, string locale,
        CancellationToken ct = default)
    {
        if (role is not (MembershipRole.Standard or MembershipRole.Approver))
        {
            return new TeamResult(false, "invalid_role", "Invite people as Standard or Approver.");
        }

        displayName = (displayName ?? "").Trim();
        email = IdentityStore.NormalizeEmail(email ?? "");
        if (displayName.Length is 0 or > 200)
        {
            return new TeamResult(false, "invalid_name", "A name is required.");
        }

        if (email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return new TeamResult(false, "invalid_email", "A valid email is required.");
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        IssuedToken issued;
        try
        {
            issued = await identity.IssueInvitationAsync(conn, tx,
                new NewInvitation(tenantId, InvitationKind.Teammate, email, displayName, role), ControlActor.User(inviterId), ct);
        }
        catch (IdentityConflictException ex) when (ex.Code == IdentityConflictException.AlreadyMember)
        {
            return new TeamResult(false, "already_member", ex.Message);
        }

        var view = new InvitationView(issued.Id, tenantId, InvitationKind.Teammate, email, displayName, role, inviterId,
            issued.ExpiresAt, TokenState.Valid);
        await outbox.EnqueueAsync(conn, tx, await invitations.InvitationEmailAsync(conn, tx, view, company, issued, locale, ct, note), ct);
        await tx.CommitAsync(ct);
        return new TeamResult(true);
    }

    /// <summary>Sends a new link for an open invitation (the old link stops working).</summary>
    public async Task<TeamResult> ResendAsync(Guid tenantId, string company, Guid invitationId, Guid actorId, string locale, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InvitationRow>(new CommandDefinition(
            """
            select id, display_name, email, role, expires_at, created_at from invitation
            where id = @invitationId and tenant_id = @tenantId and kind = 'teammate' and used_at is null and revoked_at is null
            """, new { invitationId, tenantId }, tx, cancellationToken: ct));
        if (row is null)
        {
            return new TeamResult(false, "not_found");
        }

        var issued = await identity.IssueInvitationAsync(conn, tx,
            new NewInvitation(tenantId, InvitationKind.Teammate, row.Email, row.DisplayName, row.Role), ControlActor.User(actorId), ct);
        var view = new InvitationView(issued.Id, tenantId, InvitationKind.Teammate, row.Email, row.DisplayName, row.Role, actorId,
            issued.ExpiresAt, TokenState.Valid);
        await outbox.EnqueueAsync(conn, tx, await invitations.InvitationEmailAsync(conn, tx, view, company, issued, locale, ct), ct);
        await tx.CommitAsync(ct);
        return new TeamResult(true);
    }

    public async Task<TeamResult> CancelAsync(Guid tenantId, Guid invitationId, Guid actorId, CancellationToken ct = default)
    {
        await using (var conn = await controlPlane.OpenAsync(ct))
        {
            var mine = await conn.ScalarAsync<bool>(
                "select exists (select 1 from invitation where id = @invitationId and tenant_id = @tenantId and kind = 'teammate')",
                new { invitationId, tenantId }, ct: ct);
            if (!mine)
            {
                return new TeamResult(false, "not_found");
            }
        }

        return await identity.RevokeInvitationAsync(invitationId, ControlActor.User(actorId), ct)
            ? new TeamResult(true)
            : new TeamResult(false, "not_open", "That invitation was already used or cancelled.");
    }

    public async Task<TeamResult> ChangeRoleAsync(
        Guid tenantId, string company, Guid userId, string role, Guid actorId, string locale, CancellationToken ct = default)
    {
        if (!MembershipRole.IsValid(role))
        {
            return new TeamResult(false, "invalid_role");
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var member = await LockMemberAsync(conn, tx, tenantId, userId, ct);
        if (member is null || member.Status != "active")
        {
            return new TeamResult(false, "not_found");
        }

        if (member.Role == role)
        {
            return new TeamResult(true);
        }

        if (member.Role == MembershipRole.Admin && await IsLastAdminAsync(conn, tx, tenantId, ct))
        {
            return new TeamResult(false, "last_admin", "A company needs at least one admin.");
        }

        await conn.ExecAsync(
            "update tenant_membership set role = @role, updated_at = now() where tenant_id = @tenantId and user_id = @userId",
            new { role, tenantId, userId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(actorId), "membership.role_changed", tenantId, "user_account",
            userId.ToString(), new { from = member.Role, to = role }, ct);
        var actorName = await NameAsync(conn, tx, actorId, ct);
        await outbox.EnqueueAsync(conn, tx, EmailTemplates.RoleChanged(locale, member.Email,
            new RoleChangedModel(tenantId, member.DisplayName, company, role, actorName, links.Dashboard)), ct);
        await tx.CommitAsync(ct);
        return new TeamResult(true);
    }

    /// <summary>Deactivated people can't sign in to this company; their history stays. Their sessions here end.</summary>
    public async Task<TeamResult> DeactivateAsync(
        Guid tenantId, string company, Guid userId, Guid actorId, string locale, CancellationToken ct = default)
    {
        if (userId == actorId)
        {
            return new TeamResult(false, "self", "You can't deactivate yourself.");
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var member = await LockMemberAsync(conn, tx, tenantId, userId, ct);
        if (member is null || member.Status != "active")
        {
            return new TeamResult(false, "not_found");
        }

        if (member.Role == MembershipRole.Admin && await IsLastAdminAsync(conn, tx, tenantId, ct))
        {
            return new TeamResult(false, "last_admin", "A company needs at least one admin.");
        }

        await conn.ExecAsync(
            """
            update tenant_membership set status = 'deactivated', deactivated_at = now(), deactivated_by = @actorId, updated_at = now()
            where tenant_id = @tenantId and user_id = @userId;
            update session set revoked_at = now(), revoked_reason = 'deactivated'
            where user_id = @userId and tenant_id = @tenantId and revoked_at is null;
            """,
            new { actorId, tenantId, userId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(actorId), "membership.deactivated", tenantId, "user_account",
            userId.ToString(), ct: ct);
        var actorName = await NameAsync(conn, tx, actorId, ct);
        await outbox.EnqueueAsync(conn, tx, EmailTemplates.AccountDeactivated(locale, member.Email,
            new AccountDeactivatedModel(tenantId, member.DisplayName, company, actorName)), ct);
        await tx.CommitAsync(ct);
        return new TeamResult(true);
    }

    public async Task<TeamResult> ReactivateAsync(Guid tenantId, Guid userId, Guid actorId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await conn.ExecAsync(
            """
            update tenant_membership set status = 'active', deactivated_at = null, deactivated_by = null, updated_at = now()
            where tenant_id = @tenantId and user_id = @userId and status = 'deactivated'
            """,
            new { tenantId, userId }, tx, ct);
        if (changed == 0)
        {
            return new TeamResult(false, "not_found");
        }

        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(actorId), "membership.reactivated", tenantId, "user_account",
            userId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
        return new TeamResult(true);
    }

    /// <summary>The member's email, for "Send password reset" from the row menu (only members of this company).</summary>
    public async Task<string?> MemberEmailAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.ScalarAsync<string>(
            """
            select u.email from tenant_membership m join user_account u on u.id = m.user_id
            where m.tenant_id = @tenantId and m.user_id = @userId and m.status = 'active'
            """, new { tenantId, userId }, ct: ct);
    }

    private static async Task<MemberRow?> LockMemberAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid userId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<MemberRow>(new CommandDefinition(
            """
            select u.id as user_id, u.display_name, u.email, m.role, m.status, u.last_sign_in_at
            from tenant_membership m join user_account u on u.id = m.user_id
            where m.tenant_id = @tenantId and m.user_id = @userId
            for update of m
            """, new { tenantId, userId }, tx, cancellationToken: ct));

    private static async Task<bool> IsLastAdminAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        // Lock the company's admin rows so two demotions can't race past the check.
        var admins = (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            select user_id from tenant_membership where tenant_id = @tenantId and role = 'admin' and status = 'active' for update
            """, new { tenantId }, tx, cancellationToken: ct))).Count();
        return admins <= 1;
    }

    private static async Task<string> NameAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct) =>
        await conn.ScalarAsync<string>("select display_name from user_account where id = @userId", new { userId }, tx, ct) ?? "";

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed class MemberRow
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? LastSignInAt { get; set; }
    }

    private sealed class InvitationRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
