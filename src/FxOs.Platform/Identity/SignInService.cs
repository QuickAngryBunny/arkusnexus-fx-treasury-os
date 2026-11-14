using Dapper;
using FxOs.Platform.Data;

namespace FxOs.Platform.Identity;

public enum SignInOutcome
{
    SignedIn,

    /// <summary>Unknown email, wrong password or no password yet: one answer for all, so accounts can't be probed.</summary>
    InvalidCredentials,

    /// <summary>The account is locked after repeated failures (T-S2).</summary>
    Locked,

    /// <summary>The password is right but no active membership in an active company remains (deactivated).</summary>
    NoAccess,
}

public sealed record SignInRequest(string Email, string Password, bool Remember, string? UserAgent, string? IpAddress);

public sealed record SignInResult(
    SignInOutcome Outcome, NewSession? Session = null, SessionPrincipal? Principal = null, DateTimeOffset? LockedUntil = null);

/// <summary>
/// Email-and-password sign-in. Every path spends one Argon2 verification so response time doesn't reveal whether an
/// email exists. Five wrong passwords in a row lock the account for 15 minutes (IdentityStore). A successful sign-in
/// opens a session in the user's company: the one they used last, else their first active membership.
/// </summary>
public sealed class SignInService(
    ControlPlaneDb controlPlane, IdentityStore identity, SessionStore sessions, IPasswordHasher hasher, TimeProvider time)
{
    public async Task<SignInResult> SignInAsync(SignInRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var account = string.IsNullOrWhiteSpace(request.Email) ? null : await identity.FindForSignInAsync(request.Email, ct);

        if (account is null || account.PasswordHash is null)
        {
            hasher.VerifyAgainstDummy(request.Password);
            return new SignInResult(SignInOutcome.InvalidCredentials);
        }

        if (account.IsLockedAt(now))
        {
            hasher.VerifyAgainstDummy(request.Password);
            return new SignInResult(SignInOutcome.Locked, LockedUntil: account.LockedUntil);
        }

        var verification = hasher.Verify(request.Password ?? "", account.PasswordHash);
        if (verification == PasswordVerification.Failed)
        {
            var failed = await identity.RecordFailedSignInAsync(account.Id, ct);
            return failed.Locked
                ? new SignInResult(SignInOutcome.Locked, LockedUntil: failed.LockedUntil)
                : new SignInResult(SignInOutcome.InvalidCredentials);
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Last company used, else the oldest active membership; only active companies count.
        var membership = await conn.QuerySingleOrDefaultAsync<(Guid TenantId, string Role)>(new CommandDefinition(
            """
            select m.tenant_id, m.role
            from tenant_membership m
            join tenant t on t.id = m.tenant_id and t.status = 'active'
            where m.user_id = @userId and m.status = 'active'
            order by (select max(s.created_at) from session s where s.user_id = m.user_id and s.tenant_id = m.tenant_id)
                     desc nulls last,
                     m.created_at
            limit 1
            """,
            new { userId = account.Id }, tx, cancellationToken: ct));

        if (membership.TenantId == Guid.Empty)
        {
            await ControlAudit.WriteAsync(conn, tx, ControlActor.User(account.Id), "account.sign_in_refused", null,
                "user_account", account.Id.ToString(), new { reason = "no_active_membership" }, ct);
            await tx.CommitAsync(ct);
            return new SignInResult(SignInOutcome.NoAccess);
        }

        if (verification == PasswordVerification.SucceededRehashNeeded)
        {
            await conn.ExecAsync(
                "update user_account set password_hash = @hash, updated_at = @now where id = @id",
                new { hash = hasher.Hash(request.Password!), now = now.UtcDateTime, id = account.Id }, tx, ct);
        }

        await conn.ExecAsync(
            """
            update user_account
            set failed_sign_in_count = 0, locked_until = null, last_sign_in_at = @now, updated_at = @now
            where id = @id
            """,
            new { now = now.UtcDateTime, id = account.Id }, tx, ct);

        var session = await sessions.CreateAsync(
            conn, tx, account.Id, membership.TenantId, request.Remember, request.UserAgent, request.IpAddress, ct);

        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(account.Id), "account.signed_in", membership.TenantId,
            "session", session.SessionId.ToString(), new { remember = request.Remember }, ct);
        await tx.CommitAsync(ct);

        var principal = new SessionPrincipal(
            session.SessionId, account.Id, account.Email, account.DisplayName, membership.TenantId, membership.Role, session.ExpiresAt);
        return new SignInResult(SignInOutcome.SignedIn, session, principal);
    }
}
