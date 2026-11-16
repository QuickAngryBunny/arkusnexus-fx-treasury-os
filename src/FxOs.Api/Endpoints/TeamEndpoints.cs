using FxOs.Api.Tenancy;
using FxOs.Platform.Audit;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>Settings › Team (T-01 to T-03, T-S3). Company admins only; each change is also in the company's audit log.</summary>
public static class TeamEndpoints
{
    public static RouteGroupBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant/team").WithTags("Team");

        group.MapGet("", async (ITenantContext tenant, TeamService team, CancellationToken ct) =>
        {
            var view = await team.ListAsync(tenant.Require().Id, ct);
            return Results.Ok(new { members = view.Members, invitations = view.Invitations });
        }).CompanyAdminOnly();

        group.MapPost("/invitations", async (InviteBody body, ITenantContext tenant, TeamService team, ITenantConnectionFactory db,
            IAuditWriter audit, CancellationToken ct) =>
        {
            var t = tenant.Require();
            var result = await team.InviteAsync(t.Id, t.DisplayName, tenant.Principal!.UserId, body.DisplayName ?? "", body.Email ?? "",
                body.Role ?? "", body.Note, Locale(body.Locale), ct);
            if (result.Ok)
            {
                await AuditAsync(db, audit, "team.invited", null, new { email = body.Email, body.Role }, ct);
            }

            return Answer(result);
        }).CompanyAdminOnly();

        group.MapPost("/invitations/{id:guid}/resend", async (Guid id, LocaleBody body, ITenantContext tenant, TeamService team,
            ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var t = tenant.Require();
            var result = await team.ResendAsync(t.Id, t.DisplayName, id, tenant.Principal!.UserId, Locale(body.Locale), ct);
            if (result.Ok) await AuditAsync(db, audit, "team.invitation_resent", id.ToString(), null, ct);
            return Answer(result);
        }).CompanyAdminOnly();

        group.MapPost("/invitations/{id:guid}/cancel", async (Guid id, ITenantContext tenant, TeamService team,
            ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var result = await team.CancelAsync(tenant.Require().Id, id, tenant.Principal!.UserId, ct);
            if (result.Ok) await AuditAsync(db, audit, "team.invitation_cancelled", id.ToString(), null, ct);
            return Answer(result);
        }).CompanyAdminOnly();

        group.MapPost("/members/{userId:guid}/role", async (Guid userId, RoleBody body, ITenantContext tenant, TeamService team,
            ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var t = tenant.Require();
            var result = await team.ChangeRoleAsync(t.Id, t.DisplayName, userId, body.Role ?? "", tenant.Principal!.UserId,
                Locale(body.Locale), ct);
            if (result.Ok) await AuditAsync(db, audit, "team.role_changed", userId.ToString(), new { body.Role }, ct);
            return Answer(result);
        }).CompanyAdminOnly();

        group.MapPost("/members/{userId:guid}/deactivate", async (Guid userId, LocaleBody body, ITenantContext tenant, TeamService team,
            ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var t = tenant.Require();
            var result = await team.DeactivateAsync(t.Id, t.DisplayName, userId, tenant.Principal!.UserId, Locale(body.Locale), ct);
            if (result.Ok) await AuditAsync(db, audit, "team.deactivated", userId.ToString(), null, ct);
            return Answer(result);
        }).CompanyAdminOnly();

        group.MapPost("/members/{userId:guid}/reactivate", async (Guid userId, ITenantContext tenant, TeamService team,
            ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var result = await team.ReactivateAsync(tenant.Require().Id, userId, tenant.Principal!.UserId, ct);
            if (result.Ok) await AuditAsync(db, audit, "team.reactivated", userId.ToString(), null, ct);
            return Answer(result);
        }).CompanyAdminOnly();

        // Row menu "Send password reset": the same email as "Forgot password?", for a member of this company only.
        group.MapPost("/members/{userId:guid}/password-reset", async (Guid userId, LocaleBody body, ITenantContext tenant, TeamService team,
            PasswordResetService resets, ITenantConnectionFactory db, IAuditWriter audit, CancellationToken ct) =>
        {
            var email = await team.MemberEmailAsync(tenant.Require().Id, userId, ct);
            if (email is null) return Results.NotFound();
            await resets.RequestAsync(email, Locale(body.Locale), ct);
            await AuditAsync(db, audit, "team.password_reset_sent", userId.ToString(), null, ct);
            return Results.Accepted(value: new { });
        }).CompanyAdminOnly();

        return group;
    }

    private static string Locale(string? locale) => locale is "en-US" ? "en-US" : "es-MX";

    private static async Task AuditAsync(ITenantConnectionFactory db, IAuditWriter audit, string action, string? entityId, object? data, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await audit.WriteAsync(conn, null, new AuditEntry(action, "user", entityId, data), ct);
    }

    private static IResult Answer(TeamResult result) => result.Ok
        ? Results.Ok(new { })
        : Results.Json(new { code = result.Code, message = result.Message }, statusCode: result.Code switch
        {
            "not_found" => StatusCodes.Status404NotFound,
            "already_member" or "last_admin" or "self" or "not_open" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        });
}

public sealed record InviteBody(string? DisplayName, string? Email, string? Role, string? Note, string? Locale);

public sealed record RoleBody(string? Role, string? Locale);

public sealed record LocaleBody(string? Locale);
