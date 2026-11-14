using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;

namespace FxOs.Platform.Identity;

public sealed record DevAccountResult(bool Ok, string Message, Guid? UserId = null);

/// <summary>
/// Development sign-in for dummy tenants (Migrator <c>dev-user</c>): creates or updates a login with a password the
/// developer types, adds or reactivates its membership, and writes the tenant's user profile. Refuses tenants that hold
/// real data: those get people only through invitations.
/// </summary>
public sealed class DevAccountService(
    ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, IPasswordHasher hasher)
{
    public async Task<DevAccountResult> SetAsync(
        string slug, string email, string displayName, string role, string password, CancellationToken ct = default)
    {
        var tenant = await store.FindBySlugAsync(slug, ct);
        if (tenant is null)
        {
            return new DevAccountResult(false, $"No tenant '{slug}'.");
        }

        if (!tenant.IsDummy)
        {
            return new DevAccountResult(false, $"Tenant '{slug}' holds real data. Add people there by invitation, not with dev-user.");
        }

        if (!tenant.IsActive)
        {
            return new DevAccountResult(false, $"Tenant '{slug}' is {tenant.Status}.");
        }

        if (!MembershipRole.IsValid(role))
        {
            return new DevAccountResult(false, "Role must be admin, approver or standard.");
        }

        email = IdentityStore.NormalizeEmail(email);
        displayName = displayName.Trim();
        if (displayName.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
        {
            return new DevAccountResult(false, "An email and a name are required.");
        }

        var broken = PasswordPolicy.Check(password, email, displayName);
        if (broken.Count > 0)
        {
            return new DevAccountResult(false, "The password breaks: " + string.Join(", ", broken) + ".");
        }

        var hash = hasher.Hash(password);
        Guid userId;
        await using (var conn = await controlPlane.OpenAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            userId = await conn.ScalarAsync<Guid>(
                """
                insert into user_account (email, display_name, password_hash, password_changed_at)
                values (@email, @displayName, @hash, now())
                on conflict (email) do update
                    set display_name = excluded.display_name, password_hash = excluded.password_hash,
                        password_changed_at = now(), failed_sign_in_count = 0, locked_until = null, updated_at = now()
                returning id
                """,
                new { email, displayName, hash }, tx, ct);

            await conn.ExecAsync(
                """
                insert into tenant_membership (tenant_id, user_id, role) values (@tenantId, @userId, @role)
                on conflict (tenant_id, user_id) do update
                    set role = excluded.role, status = 'active', deactivated_at = null, deactivated_by = null, updated_at = now()
                """,
                new { tenantId = tenant.Id, userId, role }, tx, ct);

            await ControlAudit.WriteAsync(conn, tx, ControlActor.System, "dev.account_set", tenant.Id, "user_account",
                userId.ToString(), new { role }, ct);
            await tx.CommitAsync(ct);
        }

        var source = await tenantDbs.GetAsync(tenant, ct);
        await using (var conn = await source.OpenConnectionAsync(ct))
        {
            // A seeded dummy profile may already use this email under another id.
            await conn.ExecuteAsync(new CommandDefinition(
                """
                delete from user_profile where email = @email and user_id <> @userId;
                insert into user_profile (user_id, email, display_name) values (@userId, @email, @displayName)
                on conflict (user_id) do update set email = excluded.email, display_name = excluded.display_name, updated_at = now();
                """,
                new { userId, email, displayName }, cancellationToken: ct));
        }

        return new DevAccountResult(true, $"{email} can sign in to {tenant.Slug} as {role}.", userId);
    }
}
