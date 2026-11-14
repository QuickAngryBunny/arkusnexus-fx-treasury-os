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

        // Forgot password (T-07 → T-08): always the same answer, whether or not the account exists.
        group.MapPost("/password/forgot", async (ForgotBody body, PasswordResetService resets, CancellationToken ct) =>
        {
            await resets.RequestAsync(body.Email, Locale(body.Locale), ct);
            return Results.Accepted(value: new { });
        });

        // What the reset page shows (T-10, or T-S1 for an expired, used or unknown link).
        group.MapGet("/password/reset", async (string? token, PasswordResetService resets, CancellationToken ct) =>
        {
            var link = await resets.DescribeAsync(token, ct);
            return Results.Ok(new { state = State(link.State), email = link.Email, name = link.DisplayName });
        });

        // Set the new password (T-10 → T-11): signs out every session and emails "password changed".
        group.MapPost("/password/reset", async (ResetBody body, PasswordResetService resets, CancellationToken ct) =>
        {
            var result = await resets.ResetAsync(body.Token, body.Password, Locale(body.Locale), ct);
            return result.Outcome switch
            {
                ResetOutcome.Changed => Results.Ok(new { email = result.Email }),
                ResetOutcome.WeakPassword => Results.Json(new { code = "weak_password", rules = result.BrokenRules },
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Json(new { code = "invalid_link", state = State(result.LinkState) }, statusCode: StatusCodes.Status410Gone),
            };
        });

        return group;
    }

    private static string Locale(string? locale) => locale is "es-MX" ? "es-MX" : "en-US";

    private static string State(TokenState state) => state switch
    {
        TokenState.Valid => "valid",
        TokenState.Expired => "expired",
        TokenState.Used => "used",
        TokenState.Revoked => "revoked",
        _ => "not_found",
    };

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

public sealed record ForgotBody(string? Email, string? Locale);

public sealed record ResetBody(string? Token, string? Password, string? Locale);
