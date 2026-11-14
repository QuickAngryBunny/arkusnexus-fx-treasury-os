using FxOs.Api.Tenancy;
using FxOs.Platform.Audit;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Sign-in, sign-out and the current session (T-06, T-S2). Called only by the web app's server, which keeps the
/// session token in an HTTP-only cookie and forwards it as <c>Authorization: Bearer</c>. These routes sit outside
/// <c>/api/tenant</c>: they find the company from the account or the session themselves.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Set by the web app's server to the browser's address and user agent (the API sees only the web server).</summary>
    public const string ClientIpHeader = "X-Client-IP";
    public const string ClientAgentHeader = "X-Client-User-Agent";

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/sign-in", async (
            SignInBody body, HttpContext http, SignInService signIn, ControlPlaneStore store, TenantContext context,
            ITenantConnectionFactory tenantDb, IAuditWriter audit, CancellationToken ct) =>
        {
            var result = await signIn.SignInAsync(
                new SignInRequest(body.Email ?? "", body.Password ?? "", body.Remember, ClientAgent(http), ClientIp(http)), ct);

            switch (result.Outcome)
            {
                case SignInOutcome.Locked:
                    return Results.Json(new { code = "locked", lockedUntil = result.LockedUntil }, statusCode: StatusCodes.Status423Locked);
                case SignInOutcome.NoAccess:
                    return Results.Json(new { code = "no_access" }, statusCode: StatusCodes.Status403Forbidden);
                case SignInOutcome.InvalidCredentials:
                    return Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var principal = result.Principal!;
            var tenant = (await store.FindByIdAsync(principal.TenantId, ct))!;

            // The company's own audit log records the sign-in too.
            context.Set(tenant, principal.Actor, principal);
            await using (var conn = await tenantDb.OpenAsync(ct))
            {
                await audit.WriteAsync(conn, null, new AuditEntry("auth.signed_in", "user", principal.UserId.ToString(),
                    new { remember = body.Remember }), ct);
            }

            return Results.Ok(new
            {
                token = result.Session!.RawToken,
                expiresAt = result.Session.ExpiresAt,
                remember = body.Remember,
                session = Describe(principal, tenant),
            });
        });

        group.MapPost("/sign-out", async (HttpContext http, SessionStore sessions, CancellationToken ct) =>
        {
            var principal = await sessions.ValidateAsync(SessionToken.FromRequest(http.Request), ct);
            if (principal is not null)
            {
                await sessions.RevokeAsync(principal.SessionId, "sign_out", ct);
            }

            // Signing out an ended session is not an error: the web app clears its cookie either way.
            return Results.NoContent();
        });

        group.MapGet("/session", async (HttpContext http, SessionStore sessions, ControlPlaneStore store, CancellationToken ct) =>
        {
            var principal = await sessions.ValidateAsync(SessionToken.FromRequest(http.Request), ct);
            if (principal is null)
            {
                return Results.Json(new { code = "signed_out" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var tenant = (await store.FindByIdAsync(principal.TenantId, ct))!;
            return Results.Ok(Describe(principal, tenant));
        });

        return group;
    }

    private static object Describe(SessionPrincipal principal, TenantRecord tenant) => new
    {
        user = new { id = principal.UserId, email = principal.Email, displayName = principal.DisplayName },
        tenant = new { id = tenant.Id, slug = tenant.Slug, displayName = tenant.DisplayName, isDummy = tenant.IsDummy },
        role = principal.Role,
        expiresAt = principal.ExpiresAt,
    };

    private static string? ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers[ClientIpHeader].ToString();
        return forwarded.Length > 0 ? forwarded.Split(',')[0].Trim() : http.Connection.RemoteIpAddress?.ToString();
    }

    private static string? ClientAgent(HttpContext http)
    {
        var forwarded = http.Request.Headers[ClientAgentHeader].ToString();
        return forwarded.Length > 0 ? forwarded : http.Request.Headers.UserAgent.ToString();
    }
}

public sealed record SignInBody(string? Email, string? Password, bool Remember);
