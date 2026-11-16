using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Tenancy;

namespace FxOs.Platform.Identity;

/// <summary>What the setup (O-02) and join (T-05) pages show for a link.</summary>
public sealed record InvitationInfo(
    TokenState State, Guid? TenantId, string? Kind, string? Email, string? DisplayName, string? Role, string? Company, string? InviterName,
    DateTimeOffset? ExpiresAt);

public enum JoinOutcome
{
    /// <summary>Joined and signed in (the password was set here).</summary>
    SignedIn,

    /// <summary>Joined an existing account that already had a password: sign in with it.</summary>
    SignInRequired,

    InvalidLink,
    WeakPassword,
    TenantUnavailable,
}

public sealed record JoinResult(
    JoinOutcome Outcome, TokenState LinkState, IReadOnlyList<string> BrokenRules, NewSession? Session = null, string? Kind = null);

/// <summary>
/// Using an invitation link: the first admin's setup link (O-01 → O-02) and a teammate's join link (T-04 → T-05).
/// Accepting creates or links the account, adds the membership, signs the person in when this link set their password,
/// and queues the emails — all in one transaction. Then the person's profile is written in the company's database.
/// An expired link can ask for a fresh one, sent to the same address.
/// </summary>
public sealed class InvitationFlow(
    ControlPlaneDb controlPlane, ControlPlaneStore store, TenantDataSources tenantDbs, IdentityStore identity,
    SessionStore sessions, IPasswordHasher hasher, EmailOutbox outbox, AppLinks links, TimeProvider time)
{
    public async Task<InvitationInfo> DescribeAsync(string? rawToken, CancellationToken ct = default)
    {
        var view = await identity.FindInvitationAsync(rawToken, ct);
        if (view is null)
        {
            return new InvitationInfo(TokenState.NotFound, null, null, null, null, null, null, null, null);
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        var names = await conn.QuerySingleAsync<(string Company, string? Inviter)>(new CommandDefinition(
            """
            select t.display_name, (select u.display_name from user_account u where u.id = @invitedBy)
            from tenant t where t.id = @tenantId
            """,
            new { tenantId = view.TenantId, invitedBy = view.InvitedBy }, cancellationToken: ct));

        // Only a usable link reveals who it is for.
        return view.State == TokenState.Valid
            ? new InvitationInfo(view.State, view.TenantId, view.Kind, view.Email, view.DisplayName, view.Role, names.Company,
                names.Inviter, view.ExpiresAt)
            : new InvitationInfo(view.State, view.TenantId, view.Kind, null, null, null, names.Company, names.Inviter, null);
    }

    public async Task<JoinResult> AcceptAsync(
        string? rawToken, string? displayName, string? password, bool remember, string locale, string? userAgent, string? ip,
        CancellationToken ct = default)
    {
        var info = await DescribeAsync(rawToken, ct);
        if (info.State != TokenState.Valid)
        {
            return new JoinResult(JoinOutcome.InvalidLink, info.State, []);
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? info.DisplayName! : displayName.Trim();
        if (name.Length > 200)
        {
            name = name[..200];
        }

        // An existing account keeps its password; the rules apply only to a password set here.
        var hasPassword = await AccountHasPasswordAsync(info.Email!, ct);
        if (!hasPassword)
        {
            var broken = PasswordPolicy.Check(password, info.Email, name);
            if (broken.Count > 0)
            {
                return new JoinResult(JoinOutcome.WeakPassword, TokenState.Valid, broken);
            }
        }

        NewSession? session = null;
        var hash = hasPassword ? null : hasher.Hash(password!);
        var result = await identity.AcceptInvitationAsync(rawToken, hash, name, async (conn, tx, accepted, token) =>
        {
            if (accepted.PasswordSetHere)
            {
                session = await sessions.CreateAsync(conn, tx, accepted.UserId, accepted.TenantId, remember, userAgent, ip, token);
            }

            if (accepted.Kind == InvitationKind.Teammate)
            {
                await outbox.EnqueueAsync(conn, tx, EmailTemplates.Welcome(locale, accepted.Email,
                    new WelcomeModel(accepted.TenantId, accepted.DisplayName, info.Company!, links.Dashboard)), token);
                if (accepted.InvitedBy is { } inviterId)
                {
                    var inviter = await conn.QuerySingleOrDefaultAsync<(string Email, string Name)>(new CommandDefinition(
                        "select email, display_name from user_account where id = @inviterId", new { inviterId }, tx, cancellationToken: token));
                    if (inviter.Email is not null)
                    {
                        await outbox.EnqueueAsync(conn, tx, EmailTemplates.MemberJoined(locale, inviter.Email,
                            new MemberJoinedModel(accepted.TenantId, inviter.Name, accepted.DisplayName, accepted.Email, info.Company!,
                                accepted.Role, links.Team)), token);
                    }
                }
            }
        }, ct);

        if (result.Outcome == AcceptOutcome.TenantUnavailable)
        {
            return new JoinResult(JoinOutcome.TenantUnavailable, result.LinkState, []);
        }

        if (result.Outcome != AcceptOutcome.Accepted)
        {
            return new JoinResult(JoinOutcome.InvalidLink, result.LinkState, []);
        }

        await WriteProfileAsync(result.UserId!.Value, info, name, locale, ct);
        return session is not null
            ? new JoinResult(JoinOutcome.SignedIn, TokenState.Valid, [], session, info.Kind)
            : new JoinResult(JoinOutcome.SignInRequired, TokenState.Valid, [], null, info.Kind);
    }

    /// <summary>O-S1 / T-S1: an expired link sends a fresh one to the same address. Returns false otherwise.</summary>
    public async Task<bool> RenewAsync(string? rawToken, string locale, CancellationToken ct = default)
    {
        var view = await identity.FindInvitationAsync(rawToken, ct);
        if (view is null || view.State != TokenState.Expired)
        {
            return false;
        }

        // Once per invitation, and only for a while after it expired; after that the company's admin (or the tenant
        // admin portal for setup links) has to invite again.
        if (view.RenewedFrom is not null || time.GetUtcNow() - view.ExpiresAt > identity.Options.InvitationRenewWindow)
        {
            return false;
        }

        var tenant = await store.FindByIdAsync(view.TenantId, ct);
        if (tenant is not { IsActive: true })
        {
            return false;
        }

        await using var conn = await controlPlane.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        IssuedToken issued;
        try
        {
            issued = await identity.IssueInvitationAsync(conn, tx,
                new NewInvitation(view.TenantId, view.Kind, view.Email, view.DisplayName, view.Role, RenewedFrom: view.Id),
                ControlActor.Anonymous, ct);
        }
        catch (IdentityConflictException)
        {
            return false; // already a member by now
        }

        await outbox.EnqueueAsync(conn, tx, await InvitationEmailAsync(conn, tx, view, tenant.DisplayName, issued, locale, ct), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>The setup or teammate invitation email for a freshly issued link.</summary>
    internal async Task<EmailMessage> InvitationEmailAsync(
        Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, InvitationView view, string company, IssuedToken issued,
        string locale, CancellationToken ct, string? note = null)
    {
        if (view.Kind == InvitationKind.TenantSetup)
        {
            return EmailTemplates.TenantSetupInvitation(locale, view.Email,
                new TenantSetupInvitationModel(view.TenantId, view.DisplayName, company, links.TenantSetup(issued.RawToken),
                    issued.ExpiresAt, EmailClock.MexicoCity));
        }

        var inviter = view.InvitedBy is { } id
            ? await conn.ScalarAsync<string>("select display_name from user_account where id = @id", new { id }, tx, ct)
            : null;
        return EmailTemplates.TeammateInvitation(locale, view.Email,
            new TeammateInvitationModel(view.TenantId, view.DisplayName, inviter ?? company, company, view.Role, note,
                links.Join(issued.RawToken), issued.ExpiresAt, EmailClock.MexicoCity));
    }

    private async Task<bool> AccountHasPasswordAsync(string email, CancellationToken ct)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.ScalarAsync<bool>(
            "select exists (select 1 from user_account where email = @email and password_hash is not null)", new { email }, ct: ct);
    }

    private async Task WriteProfileAsync(Guid userId, InvitationInfo info, string name, string locale, CancellationToken ct)
    {
        var tenant = await store.FindByIdAsync(info.TenantId!.Value, ct);
        if (tenant?.DatabaseName is null)
        {
            return;
        }

        var source = await tenantDbs.GetAsync(tenant, ct);
        await using var conn = await source.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            delete from user_profile where email = @email and user_id <> @userId;
            insert into user_profile (user_id, email, display_name, language) values (@userId, @email, @name, @locale)
            on conflict (user_id) do update set email = excluded.email, display_name = excluded.display_name, updated_at = now();
            """,
            new { userId, email = info.Email, name, locale = locale == "en-US" ? "en-US" : "es-MX" }, cancellationToken: ct));
    }
}
