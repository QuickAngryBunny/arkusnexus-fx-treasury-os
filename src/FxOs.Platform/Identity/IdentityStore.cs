using Dapper;
using FxOs.Platform.Data;
using Npgsql;

namespace FxOs.Platform.Identity;

public sealed record IdentityOptions
{
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan PasswordResetLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan EmailChangeLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Failed sign-ins in a row that lock the account.</summary>
    public int MaxFailedSignIns { get; init; } = 5;

    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(15);

    public static IdentityOptions Default { get; } = new();
}

public static class MembershipRole
{
    public const string Admin = "admin";
    public const string Approver = "approver";
    public const string Standard = "standard";

    public static bool IsValid(string? role) => role is Admin or Approver or Standard;
}

public static class InvitationKind
{
    public const string TenantSetup = "tenant_setup";
    public const string Teammate = "teammate";
}

public static class AccountTokenPurpose
{
    public const string PasswordReset = "password_reset";
    public const string EmailChange = "email_change";
}

/// <summary>Why a link does or doesn't work. Expired, used and revoked links all show O-S1 / T-S1.</summary>
public enum TokenState
{
    Valid,
    NotFound,
    Expired,
    Used,
    Revoked,
}

public sealed record IssuedToken(Guid Id, string RawToken, DateTimeOffset ExpiresAt);

public sealed record NewInvitation(Guid TenantId, string Kind, string Email, string DisplayName, string Role);

public sealed record InvitationView(
    Guid Id, Guid TenantId, string Kind, string Email, string DisplayName, string Role, Guid? InvitedBy,
    DateTimeOffset ExpiresAt, TokenState State);

public enum AcceptOutcome
{
    Accepted,
    InvalidLink,
    PasswordRequired,

    /// <summary>The tenant is archived, suspended or not set up; the link can't be used now.</summary>
    TenantUnavailable,
}

public sealed record AcceptResult(AcceptOutcome Outcome, TokenState LinkState, Guid? UserId = null, bool NewAccount = false);

public enum AccountTokenOutcome
{
    Succeeded,
    InvalidLink,
    EmailTaken,
}

public sealed record AccountTokenResult(
    AccountTokenOutcome Outcome, TokenState LinkState, Guid? UserId = null, string? OldEmail = null, string? NewEmail = null);

public sealed record SignInAccount(
    Guid Id, string Email, string DisplayName, string? PasswordHash, DateTimeOffset? LockedUntil, bool IsPlatformAdmin)
{
    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

public sealed record FailedSignIn(bool Locked, DateTimeOffset? LockedUntil);

/// <summary>Raised for requests the caller must turn into a user-facing answer (e.g. T-S3: already in the company).</summary>
public sealed class IdentityConflictException(string code, string message) : Exception(message)
{
    public const string AlreadyMember = "already_member";
    public const string InvalidRequest = "invalid_request";

    public string Code { get; } = code;
}

/// <summary>
/// Control-plane identity: invitations, password-reset and email-change tokens, and the sign-in lock.
/// Tokens are single use, expire, and issuing a new one revokes the open one. Only SHA-256 hashes are stored.
/// Every change writes control_audit_log in the same transaction. Emails go through the outbox (step 6).
/// </summary>
public sealed class IdentityStore(ControlPlaneDb controlPlane, TimeProvider time, IdentityOptions options)
{
    public IdentityStore(ControlPlaneDb controlPlane, TimeProvider time)
        : this(controlPlane, time, IdentityOptions.Default)
    {
    }

    public IdentityOptions Options => options;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- invitations

    /// <summary>Issues an invitation link. An open invitation for the same tenant and email is revoked (resend).</summary>
    public async Task<IssuedToken> IssueInvitationAsync(NewInvitation request, ControlActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!MembershipRole.IsValid(request.Role) || request.Kind is not (InvitationKind.TenantSetup or InvitationKind.Teammate))
        {
            throw new IdentityConflictException(IdentityConflictException.InvalidRequest, "Unknown role or invitation kind.");
        }

        var email = NormalizeEmail(request.Email);
        var now = time.GetUtcNow();
        var secret = SecureToken.New();

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var alreadyMember = await conn.ScalarAsync<bool>(
            """
            select exists (
                select 1 from tenant_membership m join user_account u on u.id = m.user_id
                where m.tenant_id = @tenantId and u.email = @email and m.status = 'active')
            """,
            new { tenantId = request.TenantId, email }, tx, ct);
        if (alreadyMember)
        {
            throw new IdentityConflictException(IdentityConflictException.AlreadyMember, "That email is already in the company.");
        }

        var replaced = await conn.ExecAsync(
            """
            update invitation set revoked_at = @now
            where tenant_id = @tenantId and email = @email and used_at is null and revoked_at is null
            """,
            new { now = now.UtcDateTime, tenantId = request.TenantId, email }, tx, ct);

        var expiresAt = now + options.InvitationLifetime;
        var id = await conn.ScalarAsync<Guid>(
            """
            insert into invitation (tenant_id, kind, email, display_name, role, token_hash, invited_by, expires_at, created_at)
            values (@tenantId, @kind, @email, @displayName, @role, @hash, @invitedBy, @expiresAt, @now)
            returning id
            """,
            new
            {
                tenantId = request.TenantId,
                kind = request.Kind,
                email,
                displayName = request.DisplayName.Trim(),
                role = request.Role,
                hash = secret.Hash,
                invitedBy = actor.UserId,
                expiresAt = expiresAt.UtcDateTime,
                now = now.UtcDateTime,
            },
            tx, ct);

        await ControlAudit.WriteAsync(conn, tx, actor, replaced > 0 ? "invitation.resent" : "invitation.sent", request.TenantId,
            "invitation", id.ToString(), new { request.Kind, request.Role, email }, ct);
        await tx.CommitAsync(ct);

        return new IssuedToken(id, secret.Raw, expiresAt);
    }

    /// <summary>Looks up a link for the join / setup page. Null only when the token is malformed or unknown.</summary>
    public async Task<InvitationView?> FindInvitationAsync(string? rawToken, CancellationToken ct = default)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return null;
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InvitationRow>(new CommandDefinition(
            InvitationRow.Select + " where token_hash = @hash", new { hash }, cancellationToken: ct));
        return row?.ToView(time.GetUtcNow());
    }

    /// <summary>
    /// Uses an invitation: creates the account (password required) or links an existing one (its password is kept),
    /// then adds or reactivates the membership with the invited role. Single use; safe against double submits.
    /// </summary>
    public async Task<AcceptResult> AcceptInvitationAsync(
        string? rawToken, string? passwordHash, string? displayName = null, CancellationToken ct = default)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return new AcceptResult(AcceptOutcome.InvalidLink, TokenState.NotFound);
        }

        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Row lock: a second submit of the same link waits, then sees it used.
        var invitation = await conn.QuerySingleOrDefaultAsync<InvitationRow>(new CommandDefinition(
            InvitationRow.Select + " where token_hash = @hash for update", new { hash }, tx, cancellationToken: ct));
        var state = invitation?.StateAt(now) ?? TokenState.NotFound;
        if (invitation is null || state != TokenState.Valid)
        {
            return new AcceptResult(AcceptOutcome.InvalidLink, state);
        }

        var tenantActive = await conn.ScalarAsync<bool>(
            "select status = 'active' from tenant where id = @tenantId", new { tenantId = invitation.TenantId }, tx, ct);
        if (!tenantActive)
        {
            return new AcceptResult(AcceptOutcome.TenantUnavailable, state);
        }

        var account = await conn.QuerySingleOrDefaultAsync<(Guid Id, string? PasswordHash)>(new CommandDefinition(
            "select id, password_hash from user_account where email = @email for update",
            new { email = invitation.Email }, tx, cancellationToken: ct));

        var name = string.IsNullOrWhiteSpace(displayName) ? invitation.DisplayName : displayName.Trim();
        Guid userId;
        var newAccount = false;
        if (account.Id == Guid.Empty)
        {
            if (string.IsNullOrEmpty(passwordHash))
            {
                return new AcceptResult(AcceptOutcome.PasswordRequired, state);
            }

            userId = await conn.ScalarAsync<Guid>(
                """
                insert into user_account (email, display_name, password_hash, password_changed_at, created_at, updated_at)
                values (@email, @name, @passwordHash, @now, @now, @now)
                returning id
                """,
                new { email = invitation.Email, name, passwordHash, now = now.UtcDateTime }, tx, ct);
            newAccount = true;
        }
        else
        {
            userId = account.Id;
            if (account.PasswordHash is null)
            {
                if (string.IsNullOrEmpty(passwordHash))
                {
                    return new AcceptResult(AcceptOutcome.PasswordRequired, state);
                }

                await conn.ExecAsync(
                    """
                    update user_account set password_hash = @passwordHash, password_changed_at = @now, display_name = @name,
                           updated_at = @now
                    where id = @userId
                    """,
                    new { passwordHash, now = now.UtcDateTime, name, userId }, tx, ct);
            }
        }

        await conn.ExecAsync(
            """
            insert into tenant_membership (tenant_id, user_id, role, created_at, updated_at)
            values (@tenantId, @userId, @role, @now, @now)
            on conflict (tenant_id, user_id) do update
                set role = excluded.role, status = 'active', deactivated_at = null, deactivated_by = null,
                    updated_at = excluded.updated_at
            """,
            new { tenantId = invitation.TenantId, userId, role = invitation.Role, now = now.UtcDateTime }, tx, ct);

        await conn.ExecAsync(
            "update invitation set used_at = @now, used_by = @userId where id = @id",
            new { now = now.UtcDateTime, userId, id = invitation.Id }, tx, ct);

        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(userId), "invitation.accepted", invitation.TenantId,
            "invitation", invitation.Id.ToString(), new { invitation.Kind, invitation.Role, newAccount }, ct);
        await tx.CommitAsync(ct);

        return new AcceptResult(AcceptOutcome.Accepted, TokenState.Valid, userId, newAccount);
    }

    /// <summary>Cancels an open invitation (Team row menu). Returns false when it was already used, revoked or unknown.</summary>
    public async Task<bool> RevokeInvitationAsync(Guid invitationId, ControlActor actor, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var tenantId = await conn.ScalarAsync<Guid?>(
            """
            update invitation set revoked_at = @now
            where id = @invitationId and used_at is null and revoked_at is null
            returning tenant_id
            """,
            new { now = now.UtcDateTime, invitationId }, tx, ct);
        if (tenantId is null)
        {
            return false;
        }

        await ControlAudit.WriteAsync(conn, tx, actor, "invitation.cancelled", tenantId, "invitation", invitationId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------- password reset and email change

    /// <summary>Issues a password-reset link (1 hour). An open reset link for the user is revoked.</summary>
    public Task<IssuedToken> IssuePasswordResetAsync(Guid userId, CancellationToken ct = default) =>
        IssueAccountTokenAsync(userId, AccountTokenPurpose.PasswordReset, null, options.PasswordResetLifetime,
            ControlActor.Anonymous, ct);

    /// <summary>Issues a confirmation link for a new email address (24 hours). An open one for the user is revoked.</summary>
    public Task<IssuedToken> IssueEmailChangeAsync(Guid userId, string newEmail, CancellationToken ct = default) =>
        IssueAccountTokenAsync(userId, AccountTokenPurpose.EmailChange, NormalizeEmail(newEmail), options.EmailChangeLifetime,
            ControlActor.User(userId), ct);

    public async Task<TokenState> GetAccountTokenStateAsync(string? rawToken, string purpose, CancellationToken ct = default)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return TokenState.NotFound;
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AccountTokenRow>(new CommandDefinition(
            AccountTokenRow.Select + " where token_hash = @hash and purpose = @purpose", new { hash, purpose }, cancellationToken: ct));
        return row?.StateAt(time.GetUtcNow()) ?? TokenState.NotFound;
    }

    /// <summary>
    /// Sets a new password from a reset link: single use, clears the lock, and ends every session of the user
    /// (T-09 → T-11). The caller enqueues the "password changed" email.
    /// </summary>
    public async Task<AccountTokenResult> ResetPasswordAsync(string? rawToken, string newPasswordHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPasswordHash);
        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (token, state) = await ClaimAccountTokenAsync(conn, tx, rawToken, AccountTokenPurpose.PasswordReset, now, ct);
        if (token is null)
        {
            return new AccountTokenResult(AccountTokenOutcome.InvalidLink, state);
        }

        await conn.ExecAsync(
            """
            update user_account
            set password_hash = @newPasswordHash, password_changed_at = @now, failed_sign_in_count = 0, locked_until = null,
                updated_at = @now
            where id = @userId
            """,
            new { newPasswordHash, now = now.UtcDateTime, userId = token.UserId }, tx, ct);

        var ended = await RevokeSessionsAsync(conn, tx, token.UserId, "password_changed", now, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(token.UserId), "account.password_reset", null,
            "user_account", token.UserId.ToString(), new { sessionsEnded = ended }, ct);
        await tx.CommitAsync(ct);

        return new AccountTokenResult(AccountTokenOutcome.Succeeded, TokenState.Valid, token.UserId);
    }

    /// <summary>
    /// Confirms a new email address. The caller updates the user_profile copy in each tenant and emails the result.
    /// </summary>
    public async Task<AccountTokenResult> ConfirmEmailChangeAsync(string? rawToken, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (token, state) = await ClaimAccountTokenAsync(conn, tx, rawToken, AccountTokenPurpose.EmailChange, now, ct);
        if (token is null)
        {
            return new AccountTokenResult(AccountTokenOutcome.InvalidLink, state);
        }

        var taken = await conn.ScalarAsync<bool>(
            "select exists (select 1 from user_account where email = @email and id <> @userId)",
            new { email = token.NewEmail, userId = token.UserId }, tx, ct);
        if (taken)
        {
            // Leave the link unused; nothing changes.
            await tx.RollbackAsync(ct);
            return new AccountTokenResult(AccountTokenOutcome.EmailTaken, TokenState.Valid, token.UserId);
        }

        var oldEmail = await conn.ScalarAsync<string>(
            """
            with old as (select email from user_account where id = @userId)
            update user_account set email = @newEmail, updated_at = @now where id = @userId
            returning (select email from old)
            """,
            new { userId = token.UserId, newEmail = token.NewEmail, now = now.UtcDateTime }, tx, ct);

        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(token.UserId), "account.email_changed", null,
            "user_account", token.UserId.ToString(), new { oldEmail, newEmail = token.NewEmail }, ct);
        await tx.CommitAsync(ct);

        return new AccountTokenResult(AccountTokenOutcome.Succeeded, TokenState.Valid, token.UserId, oldEmail, token.NewEmail);
    }

    // ---------------------------------------------------------------- sign-in lock

    public async Task<SignInAccount?> FindForSignInAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SignInRow>(new CommandDefinition(
            """
            select id, email, display_name, password_hash, locked_until, is_platform_admin
            from user_account where email = @email
            """,
            new { email = NormalizeEmail(email) }, cancellationToken: ct));
        return row is null
            ? null
            : new SignInAccount(row.Id, row.Email, row.DisplayName, row.PasswordHash, Utc(row.LockedUntil), row.IsPlatformAdmin);
    }

    /// <summary>
    /// Counts a wrong password. The <see cref="IdentityOptions.MaxFailedSignIns"/>th in a row locks the account for
    /// <see cref="IdentityOptions.LockDuration"/> (T-S2) and starts the count again.
    /// </summary>
    public async Task<FailedSignIn> RecordFailedSignInAsync(Guid userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QuerySingleAsync<(int Count, DateTime? LockedUntil, bool JustLocked)>(new CommandDefinition(
            """
            with prev as (select failed_sign_in_count + 1 >= @max as locks from user_account where id = @userId for update)
            update user_account
            set failed_sign_in_count = case when (select locks from prev) then 0 else failed_sign_in_count + 1 end,
                locked_until = case when (select locks from prev) then @lockUntil else locked_until end,
                updated_at = @now
            where id = @userId
            returning failed_sign_in_count, locked_until, (select locks from prev)
            """,
            new
            {
                max = options.MaxFailedSignIns,
                lockUntil = (now + options.LockDuration).UtcDateTime,
                now = now.UtcDateTime,
                userId,
            },
            tx, cancellationToken: ct));

        await ControlAudit.WriteAsync(conn, tx, ControlActor.Anonymous,
            row.JustLocked ? "account.locked" : "account.sign_in_failed", null, "user_account", userId.ToString(),
            row.JustLocked ? new { lockedUntil = Utc(row.LockedUntil) } : new { failures = row.Count }, ct);
        await tx.CommitAsync(ct);

        var lockedUntil = Utc(row.LockedUntil);
        return new FailedSignIn(lockedUntil is { } until && until > now, lockedUntil);
    }

    public async Task RecordSuccessfulSignInAsync(Guid userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecAsync(
            """
            update user_account
            set failed_sign_in_count = 0, locked_until = null, last_sign_in_at = @now, updated_at = @now
            where id = @userId
            """,
            new { now = now.UtcDateTime, userId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(userId), "account.signed_in", null,
            "user_account", userId.ToString(), ct: ct);
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- internals

    private async Task<IssuedToken> IssueAccountTokenAsync(
        Guid userId, string purpose, string? newEmail, TimeSpan lifetime, ControlActor actor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var secret = SecureToken.New();
        var expiresAt = now + lifetime;

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var replaced = await conn.ExecAsync(
            """
            update account_token set revoked_at = @now
            where user_id = @userId and purpose = @purpose and used_at is null and revoked_at is null
            """,
            new { now = now.UtcDateTime, userId, purpose }, tx, ct);

        var id = await conn.ScalarAsync<Guid>(
            """
            insert into account_token (user_id, purpose, token_hash, new_email, expires_at, created_at)
            values (@userId, @purpose, @hash, @newEmail, @expiresAt, @now)
            returning id
            """,
            new { userId, purpose, hash = secret.Hash, newEmail, expiresAt = expiresAt.UtcDateTime, now = now.UtcDateTime },
            tx, ct);

        await ControlAudit.WriteAsync(conn, tx, actor, $"account_token.{purpose}_issued", null, "user_account",
            userId.ToString(), new { replaced = replaced > 0 }, ct);
        await tx.CommitAsync(ct);

        return new IssuedToken(id, secret.Raw, expiresAt);
    }

    /// <summary>Marks a valid token used inside the caller's transaction; returns null and the reason otherwise.</summary>
    private static async Task<(AccountTokenRow? Token, TokenState State)> ClaimAccountTokenAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string? rawToken, string purpose, DateTimeOffset now, CancellationToken ct)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return (null, TokenState.NotFound);
        }

        var token = await conn.QuerySingleOrDefaultAsync<AccountTokenRow>(new CommandDefinition(
            AccountTokenRow.Select + " where token_hash = @hash and purpose = @purpose for update",
            new { hash, purpose }, tx, cancellationToken: ct));
        var state = token?.StateAt(now) ?? TokenState.NotFound;
        if (token is null || state != TokenState.Valid)
        {
            return (null, state);
        }

        await conn.ExecAsync("update account_token set used_at = @now where id = @id",
            new { now = now.UtcDateTime, id = token.Id }, tx, ct);
        return (token, TokenState.Valid);
    }

    private static Task<int> RevokeSessionsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, string reason, DateTimeOffset now, CancellationToken ct) =>
        conn.ExecAsync(
            "update session set revoked_at = @now, revoked_reason = @reason where user_id = @userId and revoked_at is null",
            new { now = now.UtcDateTime, reason, userId }, tx, ct);

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private static TokenState StateOf(DateTime? usedAt, DateTime? revokedAt, DateTime expiresAt, DateTimeOffset now) =>
        usedAt is not null ? TokenState.Used
        : revokedAt is not null ? TokenState.Revoked
        : Utc(expiresAt) <= now ? TokenState.Expired
        : TokenState.Valid;

    private sealed class InvitationRow
    {
        public const string Select = """
            select id, tenant_id, kind, email, display_name, role, invited_by, expires_at, used_at, revoked_at
            from invitation
            """;

        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Kind { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public Guid? InvitedBy { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public DateTime? RevokedAt { get; set; }

        public TokenState StateAt(DateTimeOffset now) => StateOf(UsedAt, RevokedAt, ExpiresAt, now);

        public InvitationView ToView(DateTimeOffset now) =>
            new(Id, TenantId, Kind, Email, DisplayName, Role, InvitedBy, Utc(ExpiresAt)!.Value, StateAt(now));
    }

    private sealed class AccountTokenRow
    {
        public const string Select = "select id, user_id, purpose, new_email, expires_at, used_at, revoked_at from account_token";

        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Purpose { get; set; } = "";
        public string? NewEmail { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public DateTime? RevokedAt { get; set; }

        public TokenState StateAt(DateTimeOffset now) => StateOf(UsedAt, RevokedAt, ExpiresAt, now);
    }

    private sealed class SignInRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? PasswordHash { get; set; }
        public DateTime? LockedUntil { get; set; }
        public bool IsPlatformAdmin { get; set; }
    }
}
