using FxOs.Platform.Identity;

namespace FxOs.Platform.Tenancy;

/// <summary>The tenant the current request (scope) acts for. Set once, never changed.</summary>
public interface ITenantContext
{
    TenantRecord? Current { get; }

    /// <summary>Who is acting: "user:&lt;id&gt;" for a signed-in user, "dev-switcher" on internal dummy tenants.</summary>
    string Actor { get; }

    /// <summary>The signed-in user, or null for the developer switcher.</summary>
    SessionPrincipal? Principal { get; }

    TenantRecord Require();
}

public sealed class TenantContext : ITenantContext
{
    public TenantRecord? Current { get; private set; }

    public string Actor { get; private set; } = "system";

    public SessionPrincipal? Principal { get; private set; }

    public void Set(TenantRecord tenant, string actor, SessionPrincipal? principal = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Current is not null && Current.Id != tenant.Id)
        {
            throw new TenantIsolationException("The tenant of a request cannot change once it is set.");
        }

        if (principal is not null && principal.TenantId != tenant.Id)
        {
            throw new TenantIsolationException("The session belongs to another tenant.");
        }

        Current = tenant;
        Actor = actor;
        Principal = principal;
    }

    public TenantRecord Require() =>
        Current ?? throw new TenantIsolationException("No tenant is set for this request.");
}
