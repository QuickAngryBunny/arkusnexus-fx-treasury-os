using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;

namespace FxOs.Platform.Identity;

/// <summary>Links the API puts in emails, built from <c>App:PublicBaseUrl</c> (the web app's address).</summary>
public sealed class AppLinks(Uri publicBaseUrl)
{
    public Uri Base { get; } = publicBaseUrl;

    public Uri SignIn => new(Base, "sign-in");

    public Uri ForgotPassword => new(Base, "forgot-password");

    public Uri ResetPassword(string rawToken) => new(Base, "reset-password?token=" + Uri.EscapeDataString(rawToken));

    /// <summary>The first admin's setup link (O-01 → O-02, onboarding).</summary>
    public Uri TenantSetup(string rawToken) => new(Base, "setup?token=" + Uri.EscapeDataString(rawToken));

    public Uri Admin => new(Base, "admin");

    /// <summary>A teammate's invitation (T-04 → T-05).</summary>
    public Uri Join(string rawToken) => new(Base, "join?token=" + Uri.EscapeDataString(rawToken));

    /// <summary>Confirms a new sign-in email (U-01).</summary>
    public Uri ConfirmEmail(string rawToken) => new(Base, "confirm-email?token=" + Uri.EscapeDataString(rawToken));

    public Uri Team => new(Base, "settings/team");

    public Uri Dashboard => Base;
}

public enum ResetOutcome
{
    Changed,
    InvalidLink,
    WeakPassword,
}

public sealed record ResetResult(ResetOutcome Outcome, TokenState LinkState, IReadOnlyList<string> BrokenRules, string? Email = null);

public sealed record ResetLinkInfo(TokenState State, string? Email, string? DisplayName);

/// <summary>
/// Forgot and reset password (T-07 to T-11). Requesting a link always looks the same to the caller. A link goes only to
/// accounts that can sign in somewhere (an active membership in an active company), at most once a minute per account.
/// The token and its email are written in one transaction, as are the new password and the "password changed" email.
/// </summary>
public sealed class PasswordResetService(
    ControlPlaneDb controlPlane, IdentityStore identity, IPasswordHasher hasher, EmailOutbox outbox, AppLinks links,
    TimeProvider time, EmailClocks clocks)
{
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Returns true when an email was queued; callers must not reveal it.</summary>
    public async Task<bool> RequestAsync(string? email, string locale, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Anyone who can sign in somewhere: an active membership in an active company, or a platform admin.
        var account = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Email, string DisplayName, Guid? TenantId)>(
            new CommandDefinition(
                """
                select u.id, u.email, u.display_name,
                       (select m.tenant_id from tenant_membership m join tenant t on t.id = m.tenant_id and t.status = 'active'
                        where m.user_id = u.id and m.status = 'active' order by m.created_at limit 1) as tenant_id
                from user_account u
                where u.email = @email and u.password_hash is not null
                  and (u.is_platform_admin or exists (
                        select 1 from tenant_membership m join tenant t on t.id = m.tenant_id and t.status = 'active'
                        where m.user_id = u.id and m.status = 'active'))
                for update of u
                """,
                new { email = IdentityStore.NormalizeEmail(email) }, tx, cancellationToken: ct));
        if (account.Id == Guid.Empty)
        {
            return false;
        }

        var recent = await conn.ScalarAsync<bool>(
            """
            select exists (select 1 from account_token
                           where user_id = @userId and purpose = 'password_reset' and created_at > @since)
            """,
            new { userId = account.Id, since = (now - ResendCooldown).UtcDateTime }, tx, ct);
        if (recent)
        {
            return false;
        }

        var issued = await identity.IssuePasswordResetAsync(conn, tx, account.Id, ct);
        var clock = await clocks.ForUserAsync(account.Id, account.TenantId, ct);
        await outbox.EnqueueAsync(conn, tx, EmailTemplates.PasswordReset(locale, account.Email,
            new PasswordResetModel(account.TenantId, account.DisplayName, account.Email, links.ResetPassword(issued.RawToken), now,
                clock)), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>What the reset page shows for a link (T-10, or T-S1 when it can't be used).</summary>
    public async Task<ResetLinkInfo> DescribeAsync(string? rawToken, CancellationToken ct = default)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return new ResetLinkInfo(TokenState.NotFound, null, null);
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Email, string DisplayName)>(new CommandDefinition(
            """
            select u.email, u.display_name from account_token t join user_account u on u.id = t.user_id
            where t.token_hash = @hash and t.purpose = 'password_reset'
            """,
            new { hash }, cancellationToken: ct));
        var state = await identity.GetAccountTokenStateAsync(rawToken, AccountTokenPurpose.PasswordReset, ct);
        return state == TokenState.Valid
            ? new ResetLinkInfo(state, row.Email, row.DisplayName)
            : new ResetLinkInfo(state, null, null);
    }

    public async Task<ResetResult> ResetAsync(string? rawToken, string? newPassword, string locale, CancellationToken ct = default)
    {
        var link = await DescribeAsync(rawToken, ct);
        if (link.State != TokenState.Valid)
        {
            return new ResetResult(ResetOutcome.InvalidLink, link.State, []);
        }

        var broken = PasswordPolicy.Check(newPassword, link.Email, link.DisplayName);
        if (broken.Count > 0)
        {
            return new ResetResult(ResetOutcome.WeakPassword, TokenState.Valid, broken);
        }

        var hash = hasher.Hash(newPassword!);
        var result = await identity.ResetPasswordAsync(rawToken, hash, async (conn, tx, userId, token) =>
        {
            var account = await conn.QuerySingleAsync<(string Email, string DisplayName)>(new CommandDefinition(
                "select email, display_name from user_account where id = @userId", new { userId }, tx, cancellationToken: token));
            var clock = await clocks.ForUserAsync(userId, null, token);
            await outbox.EnqueueAsync(conn, tx, EmailTemplates.PasswordChanged(locale, account.Email,
                new PasswordChangedModel(null, account.DisplayName, account.Email, time.GetUtcNow(), clock,
                    links.ForgotPassword)), token);
        }, ct);

        // The link can lose a race with a second submit between the check above and the claim.
        return result.Outcome == AccountTokenOutcome.Succeeded
            ? new ResetResult(ResetOutcome.Changed, TokenState.Valid, [], link.Email)
            : new ResetResult(ResetOutcome.InvalidLink, result.LinkState, []);
    }
}
