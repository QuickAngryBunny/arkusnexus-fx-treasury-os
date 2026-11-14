using Dapper;
using FxOs.Platform.Data;
using Npgsql;

namespace FxOs.Platform.Identity;

/// <summary>Who a valid session belongs to and which company it works in.</summary>
public sealed record SessionPrincipal(
    Guid SessionId, Guid UserId, string Email, string DisplayName, Guid TenantId, string Role, DateTimeOffset ExpiresAt)
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
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, Guid tenantId, bool remember, string? userAgent,
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

    /// <summary>The principal for a raw session token, or null when it is unknown, ended, or no longer allowed in.</summary>
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
            select s.id as session_id, s.user_id, u.email, u.display_name, s.tenant_id, m.role, s.expires_at, s.last_seen_at
            from session s
            join user_account u on u.id = s.user_id
            join tenant_membership m on m.user_id = s.user_id and m.tenant_id = s.tenant_id and m.status = 'active'
            join tenant t on t.id = s.tenant_id and t.status = 'active'
            where s.token_hash = @hash and s.revoked_at is null and s.expires_at > @now
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

        return new SessionPrincipal(row.SessionId, row.UserId, row.Email, row.DisplayName, row.TenantId, row.Role, Utc(row.ExpiresAt));
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
        public Guid TenantId { get; set; }
        public string Role { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime LastSeenAt { get; set; }
    }
}
