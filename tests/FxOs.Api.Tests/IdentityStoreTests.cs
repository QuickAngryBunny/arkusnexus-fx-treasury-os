using Dapper;
using FxOs.Platform.Data;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

public sealed class SecureTokenTests
{
    [Fact]
    public void Tokens_are_43_char_base64url_and_only_the_hash_is_derived_from_them()
    {
        var a = SecureToken.New();
        var b = SecureToken.New();

        Assert.Equal(SecureToken.RawLength, a.Raw.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Raw);
        Assert.NotEqual(a.Raw, b.Raw);
        Assert.Equal(32, a.Hash.Length);
        Assert.Equal(a.Hash, SecureToken.TryHash(a.Raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_have_no_hash(string? raw)
    {
        Assert.Null(SecureToken.TryHash(raw));
    }
}

/// <summary>Step 5: single-use links, expiry, revoke on resend, 15-minute lock, audited in the control plane.</summary>
[Collection(PlatformCollection.Name)]
public sealed class IdentityStoreTests(PlatformFixture fx)
{
    private const string Hash1 = "$argon2id$v=19$m=19456,t=2,p=1$c29tZXNhbHRzb21lc2FsdA$K13EBUiG7JV+9ZxztmHFTdb7J0WQsnj2V8bZaqyPptE";
    private const string Hash2 = "$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHRzb21lc2FsdA$gduXp+Z6iReEolmbyHn5V8s1EtJzmEvZfYoY/Fn/AeI";

    private readonly ManualTime _clock = new(DateTimeOffset.UtcNow);

    private IdentityStore Store() => new(fx.Services.GetRequiredService<ControlPlaneDb>(), _clock);

    [Fact]
    public async Task An_invitation_creates_the_account_and_membership_once()
    {
        var tenant = await fx.ProvisionAsync("join");
        var store = Store();
        var email = NewEmail();

        var issued = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, "  " + email.ToUpperInvariant(), "Leo Díaz", MembershipRole.Approver),
            ControlActor.System);

        var view = await store.FindInvitationAsync(issued.RawToken);
        Assert.NotNull(view);
        Assert.Equal((email, TokenState.Valid, "approver"), (view.Email, view.State, view.Role));
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromDays(7), issued.ExpiresAt);

        var needsPassword = await store.AcceptInvitationAsync(issued.RawToken, passwordHash: null);
        Assert.Equal(AcceptOutcome.PasswordRequired, needsPassword.Outcome);

        var accepted = await store.AcceptInvitationAsync(issued.RawToken, Hash1);
        Assert.Equal(AcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(accepted.NewAccount);

        var again = await store.AcceptInvitationAsync(issued.RawToken, Hash2);
        Assert.Equal((AcceptOutcome.InvalidLink, TokenState.Used), (again.Outcome, again.LinkState));
        Assert.Equal(TokenState.Used, (await store.FindInvitationAsync(issued.RawToken))!.State);

        await using var conn = await OpenAsync();
        var membership = await conn.QuerySingleAsync<(string Role, string Status)>(
            "select role, status from tenant_membership where tenant_id = @t and user_id = @u",
            new { t = tenant.Id, u = accepted.UserId });
        Assert.Equal(("approver", "active"), membership);
        Assert.Equal(Hash1, await conn.ExecuteScalarAsync<string>(
            "select password_hash from user_account where id = @u", new { u = accepted.UserId }));
    }

    [Fact]
    public async Task Concurrent_accepts_of_one_link_succeed_only_once()
    {
        var tenant = await fx.ProvisionAsync("race");
        var store = Store();
        var issued = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, NewEmail(), "Racer", MembershipRole.Standard), ControlActor.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.AcceptInvitationAsync(issued.RawToken, Hash1)));
        Assert.Single(results, r => r.Outcome == AcceptOutcome.Accepted);
        Assert.All(results.Where(r => r.Outcome != AcceptOutcome.Accepted), r => Assert.Equal(TokenState.Used, r.LinkState));
    }

    [Fact]
    public async Task Resending_revokes_the_old_link()
    {
        var tenant = await fx.ProvisionAsync("resend");
        var store = Store();
        var request = new NewInvitation(tenant.Id, InvitationKind.TenantSetup, NewEmail(), "Ana Ruiz", MembershipRole.Admin);

        var first = await store.IssueInvitationAsync(request, ControlActor.System);
        var second = await store.IssueInvitationAsync(request, ControlActor.System);

        Assert.Equal(TokenState.Revoked, (await store.FindInvitationAsync(first.RawToken))!.State);
        Assert.Equal(TokenState.Valid, (await store.FindInvitationAsync(second.RawToken))!.State);
        Assert.Equal(AcceptOutcome.InvalidLink, (await store.AcceptInvitationAsync(first.RawToken, Hash1)).Outcome);
        Assert.Equal(AcceptOutcome.Accepted, (await store.AcceptInvitationAsync(second.RawToken, Hash1)).Outcome);

        await using var conn = await OpenAsync();
        var actions = (await conn.QueryAsync<string>(
            "select action from control_audit_log where tenant_id = @t order by id", new { t = tenant.Id })).ToList();
        Assert.Equal(["invitation.sent", "invitation.resent", "invitation.accepted"], actions);
    }

    [Fact]
    public async Task Invitations_expire_after_seven_days()
    {
        var tenant = await fx.ProvisionAsync("expire");
        var store = Store();
        var issued = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, NewEmail(), "Late", MembershipRole.Standard), ControlActor.System);

        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        Assert.Equal(TokenState.Expired, (await store.FindInvitationAsync(issued.RawToken))!.State);
        var result = await store.AcceptInvitationAsync(issued.RawToken, Hash1);
        Assert.Equal((AcceptOutcome.InvalidLink, TokenState.Expired), (result.Outcome, result.LinkState));
    }

    [Fact]
    public async Task Cancelled_invitations_and_archived_tenants_refuse_the_link()
    {
        var tenant = await fx.ProvisionAsync("cancel");
        var store = Store();
        var cancelled = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, NewEmail(), "Gone", MembershipRole.Standard), ControlActor.System);
        Assert.True(await store.RevokeInvitationAsync(cancelled.Id, ControlActor.System));
        Assert.False(await store.RevokeInvitationAsync(cancelled.Id, ControlActor.System));
        Assert.Equal(TokenState.Revoked, (await store.AcceptInvitationAsync(cancelled.RawToken, Hash1)).LinkState);

        var pending = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, NewEmail(), "Later", MembershipRole.Standard), ControlActor.System);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync("update tenant set status = 'archived', archived_at = now() where id = @id", new { id = tenant.Id });
        }

        Assert.Equal(AcceptOutcome.TenantUnavailable, (await store.AcceptInvitationAsync(pending.RawToken, Hash1)).Outcome);
    }

    [Fact]
    public async Task Inviting_someone_already_in_the_company_is_refused_and_an_existing_account_keeps_its_password()
    {
        var first = await fx.ProvisionAsync("member");
        var second = await fx.ProvisionAsync("member");
        var store = Store();
        var email = NewEmail();

        var joined = await store.AcceptInvitationAsync((await store.IssueInvitationAsync(
            new NewInvitation(first.Id, InvitationKind.Teammate, email, "Ana", MembershipRole.Standard), ControlActor.System)).RawToken, Hash1);

        var conflict = await Assert.ThrowsAsync<IdentityConflictException>(() => store.IssueInvitationAsync(
            new NewInvitation(first.Id, InvitationKind.Teammate, email, "Ana", MembershipRole.Admin), ControlActor.System));
        Assert.Equal(IdentityConflictException.AlreadyMember, conflict.Code);

        // Same person invited to a second company: linked, not re-created, password untouched.
        var other = await store.AcceptInvitationAsync((await store.IssueInvitationAsync(
            new NewInvitation(second.Id, InvitationKind.Teammate, email, "Ana", MembershipRole.Admin), ControlActor.System)).RawToken, Hash2);
        Assert.Equal((AcceptOutcome.Accepted, false, joined.UserId), (other.Outcome, other.NewAccount, other.UserId));

        await using var conn = await OpenAsync();
        Assert.Equal(Hash1, await conn.ExecuteScalarAsync<string>(
            "select password_hash from user_account where id = @u", new { u = joined.UserId }));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_fifteen_minutes()
    {
        var store = Store();
        var userId = await CreateUserAsync(NewEmail());

        for (var i = 1; i < 5; i++)
        {
            Assert.False((await store.RecordFailedSignInAsync(userId)).Locked);
        }

        var locked = await store.RecordFailedSignInAsync(userId);
        Assert.True(locked.Locked);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(15), locked.LockedUntil!.Value, TimeSpan.FromMilliseconds(1));

        var account = await store.FindForSignInAsync(await EmailOfAsync(userId));
        Assert.True(account!.IsLockedAt(_clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.False(account.IsLockedAt(_clock.GetUtcNow()));

        // A success clears the count, so it takes five more failures to lock again.
        await store.RecordSuccessfulSignInAsync(userId);
        for (var i = 1; i < 5; i++)
        {
            Assert.False((await store.RecordFailedSignInAsync(userId)).Locked);
        }

        await using var conn = await OpenAsync();
        var actions = (await conn.QueryAsync<string>(
            "select action from control_audit_log where entity_id = @id order by id", new { id = userId.ToString() })).ToList();
        Assert.Single(actions, a => a == "account.locked");
        Assert.Contains("account.signed_in", actions);
    }

    [Fact]
    public async Task Sign_in_lookup_ignores_case_and_spaces()
    {
        var store = Store();
        var email = NewEmail();
        var userId = await CreateUserAsync(email);

        Assert.Equal(userId, (await store.FindForSignInAsync("  " + email.ToUpperInvariant() + " "))!.Id);
        Assert.Null(await store.FindForSignInAsync("nobody_" + email));
    }

    [Fact]
    public async Task A_reset_link_works_once_within_an_hour_and_ends_every_session()
    {
        var store = Store();
        var userId = await CreateUserAsync(NewEmail(), Hash1);
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync("update user_account set failed_sign_in_count = 3, locked_until = now() + interval '10 minutes' where id = @userId", new { userId });
            for (var i = 0; i < 2; i++)
            {
                await conn.ExecuteAsync(
                    "insert into session (user_id, token_hash, expires_at) values (@userId, @hash, now() + interval '1 day')",
                    new { userId, hash = SecureToken.New().Hash });
            }
        }

        var stale = await store.IssuePasswordResetAsync(userId);
        var issued = await store.IssuePasswordResetAsync(userId);
        Assert.Equal(TokenState.Revoked, await store.GetAccountTokenStateAsync(stale.RawToken, AccountTokenPurpose.PasswordReset));
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(1), issued.ExpiresAt);

        // A reset token isn't an email-change token.
        Assert.Equal(TokenState.NotFound, await store.GetAccountTokenStateAsync(issued.RawToken, AccountTokenPurpose.EmailChange));

        var reset = await store.ResetPasswordAsync(issued.RawToken, Hash2);
        Assert.Equal(AccountTokenOutcome.Succeeded, reset.Outcome);
        var reused = await store.ResetPasswordAsync(issued.RawToken, Hash1);
        Assert.Equal((AccountTokenOutcome.InvalidLink, TokenState.Used), (reused.Outcome, reused.LinkState));

        await using var check = await OpenAsync();
        var account = await check.QuerySingleAsync<(string Hash, int Failures, DateTime? LockedUntil)>(
            "select password_hash, failed_sign_in_count, locked_until from user_account where id = @userId", new { userId });
        Assert.Equal((Hash2, 0, (DateTime?)null), account);
        Assert.Equal(0L, await check.ExecuteScalarAsync<long>(
            "select count(*) from session where user_id = @userId and revoked_at is null", new { userId }));
        Assert.Equal(2L, await check.ExecuteScalarAsync<long>(
            "select count(*) from session where user_id = @userId and revoked_reason = 'password_changed'", new { userId }));
    }

    [Fact]
    public async Task Reset_links_expire_after_an_hour()
    {
        var store = Store();
        var userId = await CreateUserAsync(NewEmail(), Hash1);
        var issued = await store.IssuePasswordResetAsync(userId);

        _clock.Advance(TimeSpan.FromHours(1));
        var result = await store.ResetPasswordAsync(issued.RawToken, Hash2);
        Assert.Equal((AccountTokenOutcome.InvalidLink, TokenState.Expired), (result.Outcome, result.LinkState));
    }

    [Fact]
    public async Task Email_change_is_confirmed_once_and_never_takes_someone_elses_address()
    {
        var store = Store();
        var oldEmail = NewEmail();
        var userId = await CreateUserAsync(oldEmail);
        var newEmail = NewEmail();

        var issued = await store.IssueEmailChangeAsync(userId, " " + newEmail.ToUpperInvariant());
        var confirmed = await store.ConfirmEmailChangeAsync(issued.RawToken);
        Assert.Equal((AccountTokenOutcome.Succeeded, oldEmail, newEmail), (confirmed.Outcome, confirmed.OldEmail, confirmed.NewEmail));
        Assert.Equal(newEmail, await EmailOfAsync(userId));
        Assert.Equal(AccountTokenOutcome.InvalidLink, (await store.ConfirmEmailChangeAsync(issued.RawToken)).Outcome);

        // Someone else takes the address between the request and the confirmation: nothing changes, link stays unused.
        var taker = NewEmail();
        var pending = await store.IssueEmailChangeAsync(userId, taker);
        await CreateUserAsync(taker);
        Assert.Equal(AccountTokenOutcome.EmailTaken, (await store.ConfirmEmailChangeAsync(pending.RawToken)).Outcome);
        Assert.Equal(newEmail, await EmailOfAsync(userId));
        Assert.Equal(TokenState.Valid, await store.GetAccountTokenStateAsync(pending.RawToken, AccountTokenPurpose.EmailChange));
    }

    [Fact]
    public async Task Raw_tokens_are_never_stored()
    {
        var tenant = await fx.ProvisionAsync("raw");
        var store = Store();
        var invite = await store.IssueInvitationAsync(
            new NewInvitation(tenant.Id, InvitationKind.Teammate, NewEmail(), "Raw", MembershipRole.Standard), ControlActor.System);
        var userId = await CreateUserAsync(NewEmail());
        var reset = await store.IssuePasswordResetAsync(userId);

        await using var conn = await OpenAsync();
        var leaked = await conn.ExecuteScalarAsync<long>(
            """
            select (select count(*) from invitation where encode(token_hash, 'escape') like '%' || @a || '%')
                 + (select count(*) from account_token where encode(token_hash, 'escape') like '%' || @b || '%')
                 + (select count(*) from control_audit_log where detail::text like '%' || @a || '%' or detail::text like '%' || @b || '%')
            """,
            new { a = invite.RawToken, b = reset.RawToken });
        Assert.Equal(0L, leaked);
    }

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fx.Services.GetRequiredService<ControlPlaneDb>().OpenAsync();

    private static string NewEmail() => $"id_{Guid.NewGuid():N}@example.test";

    private async Task<Guid> CreateUserAsync(string email, string? passwordHash = null)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<Guid>(
            """
            insert into user_account (email, display_name, password_hash, password_changed_at)
            values (@email, 'Test user', @passwordHash, case when @passwordHash::text is null then null else now() end)
            returning id
            """,
            new { email, passwordHash });
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        await using var conn = await OpenAsync();
        return (await conn.ExecuteScalarAsync<string>("select email from user_account where id = @userId", new { userId }))!;
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
