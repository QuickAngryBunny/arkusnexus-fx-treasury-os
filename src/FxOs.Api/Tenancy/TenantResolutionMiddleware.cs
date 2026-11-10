using FxOs.Platform;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.Caching.Memory;

namespace FxOs.Api.Tenancy;

/// <summary>
/// Resolves the tenant for every <c>/api/tenant/*</c> request.
/// Phase 0: from the developer tenant switcher's <c>X-Tenant</c> header, on internal environments only.
/// Phase 3 replaces this with the signed-in user's tenant membership.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant";
    public const string TenantPathPrefix = "/api/tenant";
    public const string DevActor = "dev-switcher";

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(
        HttpContext http, TenantContext context, ControlPlaneStore store, IMemoryCache cache, PlatformSettings settings)
    {
        if (!http.Request.Path.StartsWithSegments(TenantPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        if (!settings.DevTenantSwitcherEnabled)
        {
            await ProblemAsync(http, StatusCodes.Status401Unauthorized, "Sign-in required",
                "Tenant data needs sign-in, which arrives in Phase 3. On internal environments, enable the developer tenant switcher.");
            return;
        }

        var slug = http.Request.Headers[HeaderName].ToString().Trim();
        if (slug.Length == 0)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "Tenant missing",
                $"Send the {HeaderName} header with a tenant slug.");
            return;
        }

        if (!TenantSlug.IsValid(slug))
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "Tenant invalid", $"'{slug}' is not a valid tenant slug.");
            return;
        }

        var cacheKey = "tenant:" + slug;
        if (!cache.TryGetValue(cacheKey, out TenantRecord? tenant) || tenant is null)
        {
            tenant = await store.FindBySlugAsync(slug, http.RequestAborted);
            if (tenant is { IsActive: true })
            {
                // Only active tenants are cached, so a newly provisioned tenant is visible at once.
                cache.Set(cacheKey, tenant, CacheFor);
            }
        }

        if (tenant is null)
        {
            await ProblemAsync(http, StatusCodes.Status404NotFound, "Tenant not found", $"No tenant '{slug}'.");
            return;
        }

        if (!tenant.IsActive)
        {
            await ProblemAsync(http, StatusCodes.Status409Conflict, "Tenant not active", $"Tenant '{slug}' is {tenant.Status}.");
            return;
        }

        // A copy, so nothing in the request can mutate the cached record.
        context.Set(tenant.Clone(), DevActor);
        await next(http);
    }

    private static Task ProblemAsync(HttpContext http, int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(http);
}
