using FxOs.Platform;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.Caching.Memory;

namespace FxOs.Api.Tenancy;

/// <summary>
/// Resolves the tenant for every <c>/api/tenant/*</c> request.
/// Normally from the signed-in user's session: the web app forwards the session cookie's token as
/// <c>Authorization: Bearer</c>, and the tenant is the session's company. On internal environments the developer
/// tenant switcher's <c>X-Tenant</c> header still works, for dummy tenants only.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant";
    public const string TenantPathPrefix = "/api/tenant";
    public const string DevActor = "dev-switcher";

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(
        HttpContext http, TenantContext context, ControlPlaneStore store, SessionStore sessions, IMemoryCache cache,
        PlatformSettings settings)
    {
        if (!http.Request.Path.StartsWithSegments(TenantPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        var token = SessionToken.FromRequest(http.Request);
        if (token is not null)
        {
            var principal = await sessions.ValidateAsync(token, http.RequestAborted);
            if (principal is null)
            {
                await ProblemAsync(http, StatusCodes.Status401Unauthorized, "Session ended",
                    "The session is unknown, expired or no longer allowed. Sign in again.");
                return;
            }

            var own = await FindAsync(cache, "tenant-id:" + principal.TenantId,
                () => store.FindByIdAsync(principal.TenantId, http.RequestAborted));
            if (own is not { IsActive: true })
            {
                await ProblemAsync(http, StatusCodes.Status401Unauthorized, "Session ended", "The company is not active.");
                return;
            }

            context.Set(own.Clone(), principal.Actor, principal);
            await next(http);
            return;
        }

        var slug = http.Request.Headers[HeaderName].ToString().Trim();
        if (!settings.DevTenantSwitcherEnabled || slug.Length == 0)
        {
            await ProblemAsync(http, StatusCodes.Status401Unauthorized, "Sign-in required", "Sign in to see your company's data.");
            return;
        }

        if (!TenantSlug.IsValid(slug))
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "Tenant invalid", $"'{slug}' is not a valid tenant slug.");
            return;
        }

        var tenant = await FindAsync(cache, "tenant:" + slug, () => store.FindBySlugAsync(slug, http.RequestAborted));
        if (tenant is null)
        {
            await ProblemAsync(http, StatusCodes.Status404NotFound, "Tenant not found", $"No tenant '{slug}'.");
            return;
        }

        if (!tenant.IsDummy)
        {
            // Real company data is never reachable without a signed-in member, switcher or not.
            await ProblemAsync(http, StatusCodes.Status401Unauthorized, "Sign-in required",
                "This tenant holds real data; the developer switcher only opens dummy tenants.");
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

    private static async Task<TenantRecord?> FindAsync(IMemoryCache cache, string key, Func<Task<TenantRecord?>> load)
    {
        if (cache.TryGetValue(key, out TenantRecord? tenant) && tenant is not null)
        {
            return tenant;
        }

        tenant = await load();
        if (tenant is { IsActive: true })
        {
            // Only active tenants are cached, so a newly provisioned tenant is visible at once.
            cache.Set(key, tenant, CacheFor);
        }

        return tenant;
    }

    private static Task ProblemAsync(HttpContext http, int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(http);
}

/// <summary>The session token the web app forwards from its HTTP-only cookie.</summary>
public static class SessionToken
{
    public static string? FromRequest(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string Scheme = "Bearer ";
        return header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) && header.Length > Scheme.Length
            ? header[Scheme.Length..].Trim()
            : null;
    }
}
