using FxOs.Platform.Tenancy;

namespace FxOs.Api.Tenancy;

/// <summary>
/// Marks a tenant route that only the company's admins may call (Team, company info, banks, onboarding). Enforced by
/// <see cref="CompanyAdminOnlyExtensions.CompanyAdminOnly"/>; RoleTests finds every route carrying it and checks that
/// approvers and standard members get 403.
/// </summary>
public sealed class CompanyAdminOnlyAttribute : Attribute;

public static class CompanyAdminOnlyExtensions
{
    public static TBuilder CompanyAdminOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new CompanyAdminOnlyAttribute());
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            // The developer switcher has no signed-in person, so it is never an admin here.
            return tenant.Principal is { IsAdmin: true }
                ? await next(context)
                : Results.Json(new { code = "admins_only" }, statusCode: StatusCodes.Status403Forbidden);
        });
        return builder;
    }
}
