using FxOs.Platform.Data;
using FxOs.Platform.Email;
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

        // Queues a test email through the outbox; the dispatcher sends it if the address is in Email:AllowedRecipients.
        group.MapPost("/email/test", async (TestEmailRequest body, EmailOutbox outbox, EmailSettings email, ControlPlaneDb db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.To) || !body.To.Contains('@', StringComparison.Ordinal))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["An email address is required."] });
            }

            await using var conn = await db.OpenAsync(ct);
            var id = await outbox.EnqueueAsync(conn, null, new EmailMessage(
                "dev_test", "en-US", body.To, "FX Intelligence test email",
                "<p>This is a test email from the FX Intelligence API (local development).</p>",
                "This is a test email from the FX Intelligence API (local development)."), ct);
            return Results.Accepted(value: new
            {
                id,
                allowed = email.IsAllowed(body.To),
                dispatcherRunning = email.Dispatcher.Enabled && email.Firebase.IsConfigured,
            });
        });

        return group;
    }
}

public sealed record ProvisionTenantRequest(string Slug, string DisplayName);

public sealed record TestEmailRequest(string To);
