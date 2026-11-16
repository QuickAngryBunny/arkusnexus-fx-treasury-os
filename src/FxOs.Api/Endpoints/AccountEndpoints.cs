using FxOs.Api.Tenancy;
using FxOs.Platform.Identity;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Invitation links (O-02, O-S1, T-05, T-S1), password change (U-02) and email change (U-01). Outside /api/tenant:
/// links work signed out, and account changes belong to the person, not to a company.
/// </summary>
public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Account");

        group.MapGet("/invitation", async (string? token, InvitationFlow flow, CancellationToken ct) =>
        {
            var info = await flow.DescribeAsync(token, ct);
            return Results.Ok(new
            {
                state = State(info.State), info.Kind, info.Email, displayName = info.DisplayName, info.Role, info.Company,
                inviterName = info.InviterName, info.ExpiresAt,
            });
        });

        group.MapPost("/invitation/accept", async (AcceptBody body, HttpContext http, InvitationFlow flow, CancellationToken ct) =>
        {
            var result = await flow.AcceptAsync(body.Token, body.DisplayName, body.Password, body.Remember, Locale(body.Locale),
                http.Request.Headers[AuthEndpoints.ClientAgentHeader].ToString(), ClientIp(http), ct);
            return result.Outcome switch
            {
                JoinOutcome.SignedIn => Results.Ok(new { token = result.Session!.RawToken, expiresAt = result.Session.ExpiresAt, kind = result.Kind }),
                JoinOutcome.SignInRequired => Results.Json(new { code = "sign_in_required", kind = result.Kind }, statusCode: StatusCodes.Status202Accepted),
                JoinOutcome.WeakPassword => Results.Json(new { code = "weak_password", rules = result.BrokenRules }, statusCode: StatusCodes.Status400BadRequest),
                JoinOutcome.TenantUnavailable => Results.Json(new { code = "tenant_unavailable" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { code = "invalid_link", state = State(result.LinkState) }, statusCode: StatusCodes.Status410Gone),
            };
        });

        // O-S1 / T-S1: an expired link asks for a fresh one, sent to the same address.
        group.MapPost("/invitation/renew", async (TokenBody body, InvitationFlow flow, CancellationToken ct) =>
            Results.Ok(new { sent = await flow.RenewAsync(body.Token, Locale(body.Locale), ct) }));

        group.MapPost("/password/change", async (ChangePasswordBody body, HttpContext http, SessionStore sessions, AccountService accounts,
            CancellationToken ct) =>
        {
            var who = await sessions.ValidateAsync(SessionToken.FromRequest(http.Request), ct);
            if (who is null) return Results.Json(new { code = "signed_out" }, statusCode: StatusCodes.Status401Unauthorized);
            var result = await accounts.ChangePasswordAsync(who, body.Current, body.Password, Locale(body.Locale), ct);
            return result.Ok
                ? Results.NoContent()
                : Results.Json(new { code = result.Code, rules = result.Rules }, statusCode: StatusCodes.Status400BadRequest);
        });

        group.MapPost("/email/change", async (ChangeEmailBody body, HttpContext http, SessionStore sessions, AccountService accounts,
            CancellationToken ct) =>
        {
            var who = await sessions.ValidateAsync(SessionToken.FromRequest(http.Request), ct);
            if (who is null) return Results.Json(new { code = "signed_out" }, statusCode: StatusCodes.Status401Unauthorized);
            var result = await accounts.RequestEmailChangeAsync(who, body.Email, Locale(body.Locale), ct);
            return result.Ok
                ? Results.Accepted(value: new { })
                : Results.Json(new { code = result.Code }, statusCode: result.Code == "email_taken" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        });

        group.MapPost("/email/confirm", async (TokenBody body, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.ConfirmEmailChangeAsync(body.Token, ct);
            return result.Outcome switch
            {
                AccountTokenOutcome.Succeeded => Results.Ok(new { email = result.NewEmail }),
                AccountTokenOutcome.EmailTaken => Results.Json(new { code = "email_taken" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { code = "invalid_link", state = State(result.LinkState) }, statusCode: StatusCodes.Status410Gone),
            };
        });

        return group;
    }

    private static string Locale(string? locale) => locale is "en-US" ? "en-US" : "es-MX";

    private static string? ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers[AuthEndpoints.ClientIpHeader].ToString();
        return forwarded.Length > 0 ? forwarded.Split(',')[0].Trim() : http.Connection.RemoteIpAddress?.ToString();
    }

    private static string State(TokenState state) => state switch
    {
        TokenState.Valid => "valid",
        TokenState.Expired => "expired",
        TokenState.Used => "used",
        TokenState.Revoked => "revoked",
        _ => "not_found",
    };
}

public sealed record AcceptBody(string? Token, string? DisplayName, string? Password, bool Remember, string? Locale);

public sealed record TokenBody(string? Token, string? Locale);

public sealed record ChangePasswordBody(string? Current, string? Password, string? Locale);

public sealed record ChangeEmailBody(string? Email, string? Locale);
