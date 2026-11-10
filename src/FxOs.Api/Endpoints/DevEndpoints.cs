using FxOs.Platform.Tenancy;

namespace FxOs.Api.Endpoints;

/// <summary>
/// Developer-only endpoints behind the tenant switcher. Mapped only when Platform:DevTenantSwitcher:Enabled is true,
/// which the platform allows only on internal environments. Removed in Phase 3.
/// </summary>
public static class DevEndpoints
{
    public static RouteGroupBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev").WithTags("Developer");

        group.MapGet("/tenants", async (ControlPlaneStore store, CancellationToken ct) =>
        {
            var tenants = await store.ListAsync(ct);
            return Results.Ok(tenants.Select(t => new
            {
                t.Slug,
                t.DisplayName,
                t.Status,
                t.IsDummy,
                t.SchemaVersion,
            }));
        });

        group.MapPost("/tenants", async (ProvisionTenantRequest body, TenantProvisioner provisioner, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!TenantSlug.IsValid(body.Slug))
            {
                errors["slug"] = ["3–31 characters: lowercase letters, digits or underscore, starting with a letter."];
            }

            if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 200)
            {
                errors["displayName"] = ["Display name is required (max 200 characters)."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // Tenants created here are always dummy tenants.
            var tenant = await provisioner.ProvisionAsync(body.Slug, body.DisplayName, isDummy: true, ct);
            return Results.Ok(new { tenant.Slug, tenant.DisplayName, tenant.Status, tenant.DatabaseName, tenant.SchemaVersion });
        });

        return group;
    }
}

public sealed record ProvisionTenantRequest(string Slug, string DisplayName);
