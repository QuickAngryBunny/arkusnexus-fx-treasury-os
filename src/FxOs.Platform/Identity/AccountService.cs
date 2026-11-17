using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Tenancy;

namespace FxOs.Platform.Identity;

public sealed record AccountResult(bool Ok, string? Code = null, IReadOnlyList<string>? Rules = null);

/// <summary>
/// A signed-in person's own account (U-01, U-02): name, sign-in email (changed only after a confirmation link) and
/// password (needs the current one; ends their other sessions and emails a confirmation).
/// </summary>
public sealed class AccountService(
    ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, IdentityStore identity,
    IPasswordHasher hasher, EmailOutbox outbox, AppLinks links, TimeProvider time, EmailClocks clocks)
{
    public async Task<AccountResult> ChangePasswordAsync(
        SessionPrincipal who, string? current, string? next, string locale, CancellationToken ct = default)
    {
        var clock = await clocks.ForUserAsync(who.UserId, who.TenantId, ct);
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var hash = await conn.ScalarAsync<string>(
            "select password_hash from user_account where id = @id for update", new { id = who.UserId }, tx, ct);
        if (hash is null || hasher.Verify(current ?? "", hash) == PasswordVerification.Failed)
        {
            return new AccountResult(false, "wrong_password");
        }

        var broken = PasswordPolicy.Check(next, who.Email, who.DisplayName);
        if (broken.Count > 0)
        {
            return new AccountResult(false, "weak_password", broken);
        }

        // A reset link issued before the change would otherwise still work for its last hour.
        await conn.ExecAsync(
            """
            update user_account set password_hash = @hash, password_changed_at = now(), updated_at = now() where id = @id;
            update session set revoked_at = now(), revoked_reason = 'password_changed'
            where user_id = @id and revoked_at is null and id <> @sessionId;
            update account_token set revoked_at = now()
            where user_id = @id and purpose = 'password_reset' and used_at is null and revoked_at is null;
            """,
            new { hash = hasher.Hash(next!), id = who.UserId, sessionId = who.SessionId }, tx, ct);
        await ControlAudit.WriteAsync(conn, tx, ControlActor.User(who.UserId), "account.password_changed", who.TenantId,
            "user_account", who.UserId.ToString(), ct: ct);
        await outbox.EnqueueAsync(conn, tx, EmailTemplates.PasswordChanged(locale, who.Email,
            new PasswordChangedModel(who.TenantId, who.DisplayName, who.Email, time.GetUtcNow(), clock,
                links.ForgotPassword)), ct);
        await tx.CommitAsync(ct);
        return new AccountResult(true);
    }

    public async Task<DateTimeOffset?> PasswordChangedAtAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var at = await conn.ScalarAsync<DateTime?>("select password_changed_at from user_account where id = @userId", new { userId }, ct: ct);
        return at is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
    }

    /// <summary>The name shown everywhere: control plane and every company profile.</summary>
    public async Task UpdateNameAsync(Guid userId, string name, CancellationToken ct = default)
    {
        await using (var conn = await controlPlane.OpenAsync(ct))
        {
            await conn.ExecAsync("update user_account set display_name = @name, updated_at = now() where id = @userId",
                new { name, userId }, ct: ct);
        }

        await ForEachCompanyAsync(userId, "update user_profile set display_name = @name, updated_at = now() where user_id = @userId",
            new { name, userId }, ct);
    }

    /// <summary>
    /// U-01: needs the current password (a session left open elsewhere can't move the account), then sends a
    /// confirmation link to the new address; nothing changes until it is used.
    /// </summary>
    public async Task<AccountResult> RequestEmailChangeAsync(
        SessionPrincipal who, string? newEmail, string? currentPassword, string locale, CancellationToken ct = default)
    {
        var email = IdentityStore.NormalizeEmail(newEmail ?? "");
        if (email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return new AccountResult(false, "invalid_email");
        }

        if (email == who.Email)
        {
            return new AccountResult(false, "same_email");
        }

        var clock = await clocks.ForUserAsync(who.UserId, who.TenantId, ct);
        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var hash = await conn.ScalarAsync<string>(
            "select password_hash from user_account where id = @id", new { id = who.UserId }, tx, ct);
        if (hash is null || hasher.Verify(currentPassword ?? "", hash) == PasswordVerification.Failed)
        {
            return new AccountResult(false, "wrong_password");
        }

        if (await conn.ScalarAsync<bool>("select exists (select 1 from user_account where email = @email)", new { email }, tx, ct))
        {
            return new AccountResult(false, "email_taken");
        }

        var issued = await identity.IssueEmailChangeAsync(conn, tx, who.UserId, email, ct);
        await outbox.EnqueueAsync(conn, tx, EmailTemplates.EmailChangeConfirmation(locale, email,
            new EmailChangeConfirmationModel(who.DisplayName, email, links.ConfirmEmail(issued.RawToken), issued.ExpiresAt,
                clock)), ct);
        await tx.CommitAsync(ct);
        return new AccountResult(true);
    }

    /// <summary>The confirmation link: switches the sign-in email and every company profile's copy.</summary>
    public async Task<AccountTokenResult> ConfirmEmailChangeAsync(string? rawToken, CancellationToken ct = default)
    {
        var result = await identity.ConfirmEmailChangeAsync(rawToken, ct);
        if (result.Outcome == AccountTokenOutcome.Succeeded)
        {
            await ForEachCompanyAsync(result.UserId!.Value,
                "update user_profile set email = @email, updated_at = now() where user_id = @userId",
                new { email = result.NewEmail, userId = result.UserId }, ct);
        }

        return result;
    }

    private async Task ForEachCompanyAsync(Guid userId, string sql, object args, CancellationToken ct)
    {
        List<Guid> tenantIds;
        await using (var conn = await controlPlane.OpenAsync(ct))
        {
            tenantIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                "select tenant_id from tenant_membership where user_id = @userId", new { userId }, cancellationToken: ct))).ToList();
        }

        foreach (var tenantId in tenantIds)
        {
            var tenant = await store.FindByIdAsync(tenantId, ct);
            if (tenant?.DatabaseName is null || tenant.Status == TenantStatus.Deleted)
            {
                continue;
            }

            var source = await tenantDbs.GetAsync(tenant, ct);
            await using var tconn = await source.OpenConnectionAsync(ct);
            await tconn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
        }
    }
}
