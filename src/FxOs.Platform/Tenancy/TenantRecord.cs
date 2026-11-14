namespace FxOs.Platform.Tenancy;

/// <summary>A tenant as the control plane knows it, including where its database lives.</summary>
public sealed class TenantRecord
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsDummy { get; set; }

    // Routing (null until the database exists).
    public string? ServerKey { get; set; }
    public string? DatabaseName { get; set; }
    public string? LoginRole { get; set; }
    public string? SecretRef { get; set; }
    public int? SchemaVersion { get; set; }

    public bool IsActive => Status == TenantStatus.Active;

    public TenantRecord Clone() => (TenantRecord)MemberwiseClone();
}

public static class TenantStatus
{
    public const string Provisioning = "provisioning";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Failed = "failed";
    public const string Archived = "archived";
}

public sealed class TenantIsolationException(string message) : Exception(message);
