using System.Text.Json;
using FxOs.Api.Tenancy;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Tenant admin portal (A-01 to A-04, A-S1 to A-S3). Platform admins only: every route checks the session's
/// is_platform_admin flag, whatever company the session is in. Outside /api/tenant, so no tenant is resolved.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Tenant admin");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var principal = await http.RequestServices.GetRequiredService<SessionStore>()
                .ValidateAsync(SessionToken.FromRequest(http.Request), http.RequestAborted);
            if (principal is null)
            {
                return Results.Json(new { code = "signed_out" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!principal.IsPlatformAdmin)
            {
                return Results.Json(new { code = "not_platform_admin" }, statusCode: StatusCodes.Status403Forbidden);
            }

            http.Items[nameof(SessionPrincipal)] = principal;
            return await next(context);
        });

        group.MapGet("/tenants", async (TenantAdminService admin, CancellationToken ct) =>
            Results.Ok((await admin.ListAsync(ct)).Select(Describe)));

        group.MapGet("/tenants/{id:guid}", async (Guid id, TenantAdminService admin, CancellationToken ct) =>
            await admin.GetAsync(id, ct) is { } tenant ? Results.Ok(Describe(tenant)) : Results.NotFound());

        group.MapPost("/tenants", async (CreateTenantBody body, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.CreateAsync(body.Name ?? "", body.FirstAdminEmail ?? "", !body.RealData, Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/retry", async (Guid id, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.RetrySetupAsync(id, Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/rename", async (Guid id, RenameBody body, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.RenameAsync(id, body.Name ?? "", Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/archive", async (Guid id, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.ArchiveAsync(id, Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/restore", async (Guid id, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.RestoreAsync(id, Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/delete", async (Guid id, DeleteBody body, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.DeleteAsync(id, body.ConfirmName ?? "", Actor(http), ct)));

        group.MapPost("/tenants/{id:guid}/dummy-data", async (Guid id, DummyBody body, HttpContext http, TenantAdminService admin, CancellationToken ct) =>
            Answer(await admin.RequestDummyDataAsync(id, body.Load, Actor(http), ct)));

        return group;
    }

    private static Guid Actor(HttpContext http) => ((SessionPrincipal)http.Items[nameof(SessionPrincipal)]!).UserId;

    private static IResult Answer(AdminResult result) => result.Ok
        ? Results.Ok(new { id = result.TenantId })
        : Results.Json(new { code = result.Code, message = result.Message }, statusCode: result.Code switch
        {
            "not_found" => StatusCodes.Status404NotFound,
            "real_data" or "wrong_status" or "busy" or "not_active" or "nothing_failed" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        });

    private static object Describe(TenantSummary t) => new
    {
        t.Id, t.Slug, t.DisplayName, t.Status, t.IsDummy, t.CreatedAt, t.SchemaVersion, t.Members,
        setup = Job(t.Setup),
        dummyJob = Job(t.DummyJob),
    };

    private static object? Job(TenantJobView? j) => j is null ? null : new
    {
        j.Id, j.Kind, j.Step, j.Status, j.Progress, j.Error, j.FirstAdminEmail, j.CreatedAt, j.FinishedAt,
        events = JsonSerializer.Deserialize<JsonElement>(j.EventsJson),
    };
}

public sealed record CreateTenantBody(string? Name, string? FirstAdminEmail, bool RealData);

public sealed record RenameBody(string? Name);

public sealed record DeleteBody(string? ConfirmName);

public sealed record DummyBody(bool Load);
