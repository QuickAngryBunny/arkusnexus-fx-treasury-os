namespace FxOs.Platform.Tenancy;

/// <summary>The tenant the current request (scope) acts for. Set once, never changed.</summary>
public interface ITenantContext
{
    TenantRecord? Current { get; }

    /// <summary>Who is acting: "dev-switcher" in Phase 0, the signed-in user from Phase 3.</summary>
    string Actor { get; }

    TenantRecord Require();
}

public sealed class TenantContext : ITenantContext
{
    public TenantRecord? Current { get; private set; }

    public string Actor { get; private set; } = "system";

    public void Set(TenantRecord tenant, string actor)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Current is not null && Current.Id != tenant.Id)
        {
            throw new TenantIsolationException("The tenant of a request cannot change once it is set.");
        }

        Current = tenant;
        Actor = actor;
    }

    public TenantRecord Require() =>
        Current ?? throw new TenantIsolationException("No tenant is set for this request.");
}
