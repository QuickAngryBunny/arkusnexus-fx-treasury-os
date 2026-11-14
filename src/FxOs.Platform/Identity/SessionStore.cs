using Dapper;
using FxOs.Platform.Data;
using Npgsql;

namespace FxOs.Platform.Identity;

/// <summary>
/// Who a valid session belongs to and which company it works in. <see cref="TenantId"/> is null for a platform admin
/// signed in to the tenant admin portal only; <see cref="Role"/> is then null too.
/// </summary>
public sealed record SessionPrincipal(
    Guid SessionId, Guid UserId, string Email, string DisplayName, Guid? TenantId, string? Role, DateTimeOffset ExpiresAt,
    bool IsPlatformAdmin = false)
{
    /// <summary>The actor written to tenant audit logs.</summary>
    public string Actor => "user:" + UserId.ToString("D");

    public bool IsAdmin => Role == MembershipRole.Admin;
}

public sealed record NewSession(Guid SessionId, string RawToken, DateTimeOffset ExpiresAt);

public sealed record SessionOptions
{
    /// <summary>"Keep me signed in on this computer" (T-06).</summary>
    public TimeSpan Remembered { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Otherwise the cookie ends with the browser, and the server ends the session after this at most.</summary>
    public TimeSpan BrowserSession { get; init; } = TimeSpan.FromHours(12);

    /// <summary>last_seen_at is written at most this often per session.</summary>
    public TimeSpan TouchEvery { get; init; } = TimeSpan.FromMinutes(1);

    public static SessionOptions Default { get; } = new();
}

/// <summary>
/// Browser sessions in the control plane. The raw token lives only in the HTTP-only cookie (set by the web app);
/// the database keeps its SHA-256 hash. A session is valid only while it is unexpired and unrevoked, the membership
/// is active and the tenant is active.
/// </summary>
public sealed class SessionStore(ControlPlaneDb controlPlane, TimeProvider time, SessionOptions options)
{
    public SessionStore(ControlPlaneDb controlPlane, TimeProvider time)
        : this(controlPlane, time, SessionOptions.Default)
    {
    }

    public async Task<NewSession> CreateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, Guid? tenantId, bool remember, string? userAgent,
        string? ipAddress, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var secret = SecureToken.New();
        var expiresAt = now + (remember ? options.Remembered : options.BrowserSession);
        var id = await conn.ScalarAsync<Guid>(
            """
            insert into session (user_id, tenant_id, token_hash, created_at, last_seen_at, expires_at, user_agent, ip_address)
            values (@userId, @tenantId, @hash, @now, @now, @expiresAt, @userAgent, cast(@ip as inet))
            returning id
            """,
            new
            {
                userId,
                tenantId,
                hash = secret.Hash,
                now = now.UtcDateTime,
                expiresAt = expiresAt.UtcDateTime,
                userAgent = Truncate(userAgent, 512),
                ip = ValidIp(ipAddress),
            },
            tx, ct);
        return new NewSession(id, secret.Raw, expiresAt);
    }

    /// <summary>
    /// The principal for a raw session token, or null when it is unknown, ended, or no longer allowed in. A session's
    /// company is allowed while the membership and the company are active; a platform admin may also work in an active
    /// dummy company without a membership (as its admin), or in no company at all (the portal). Real-data companies
    /// always need a membership.
    /// </summary>
    public async Task<SessionPrincipal?> ValidateAsync(string? rawToken, CancellationToken ct = default)
    {
        var hash = SecureToken.TryHash(rawToken);
        if (hash is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        await using var conn = await controlPlane.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            select s.id as session_id, s.user_id, u.email, u.display_name, s.tenant_id, u.is_platform_admin,
                   case when m.role is not null then m.role when u.is_platform_admin and t.is_dummy then 'admin' end as role,
                   s.expires_at, s.last_seen_at
            from session s
            join user_account u on u.id = s.user_id
            left join tenant t on t.id = s.tenant_id
            left join tenant_membership m on m.user_id = s.user_id and m.tenant_id = s.tenant_id and m.status = 'active'
            where s.token_hash = @hash and s.revoked_at is null and s.expires_at > @now
              and (
                    (s.tenant_id is null and u.is_platform_admin)
                 or (t.status = 'active' and (m.role is not null or (u.is_platform_admin and t.is_dummy)))
              )
            """,
            new { hash, now = now.UtcDateTime }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        if (now - Utc(row.LastSeenAt) >= options.TouchEvery)
        {
            await conn.ExecAsync("update session set last_seen_at = @now where id = @id",
                new { now = now.UtcDateTime, id = row.SessionId }, ct: ct);
        }

        return new SessionPrincipal(
            row.SessionId, row.UserId, row.Email, row.DisplayName, row.TenantId, row.Role, Utc(row.ExpiresAt), row.IsPlatformAdmin);
    }

    /// <summary>Moves a session to another company (tenant menu, A-05). The caller checks the person may work there.</summary>
    public async Task SwitchTenantAsync(Guid sessionId, Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync("update session set tenant_id = @tenantId where id = @sessionId and revoked_at is null",
            new { tenantId, sessionId }, ct: ct);
    }

    public async Task<bool> RevokeAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.ExecAsync(
            "update session set revoked_at = @now, revoked_reason = @reason where id = @sessionId and revoked_at is null",
            new { now = time.GetUtcNow().UtcDateTime, reason, sessionId }, ct: ct) > 0;
    }

    /// <summary>Ends every open session of a user except <paramref name="keep"/>, inside the caller's transaction.</summary>
    public async Task<int> RevokeAllAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, string reason, Guid? keep = null, CancellationToken ct = default) =>
        await conn.ExecAsync(
            """
            update session set revoked_at = @now, revoked_reason = @reason
            where user_id = @userId and revoked_at is null and (@keep::uuid is null or id <> @keep)
            """,
            new { now = time.GetUtcNow().UtcDateTime, reason, userId, keep }, tx, ct);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    private static string? ValidIp(string? ip) =>
        System.Net.IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : null;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class Row
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public Guid? TenantId { get; set; }
        public string? Role { get; set; }
        public bool IsPlatformAdmin { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime LastSeenAt { get; set; }
    }
}
