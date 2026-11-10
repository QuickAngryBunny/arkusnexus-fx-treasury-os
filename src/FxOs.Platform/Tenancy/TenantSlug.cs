using System.Text.RegularExpressions;

namespace FxOs.Platform.Tenancy;

/// <summary>
/// Tenant slugs become part of database and role names, so they are validated strictly
/// before they reach any SQL identifier.
/// </summary>
public static partial class TenantSlug
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,30}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? slug) => slug is not null && Pattern().IsMatch(slug);

    public static void EnsureValid(string? slug)
    {
        if (!IsValid(slug))
        {
            throw new ArgumentException(
                $"Invalid tenant slug '{slug}'. Use 3–31 characters: lowercase letters, digits or underscore, starting with a letter.",
                nameof(slug));
        }
    }

    public static string DatabaseName(string slug)
    {
        EnsureValid(slug);
        return $"fx_t_{slug}";
    }

    public static string LoginRole(string slug)
    {
        EnsureValid(slug);
        return $"fx_t_{slug}_app";
    }

    public static string SecretRef(string slug)
    {
        EnsureValid(slug);
        return $"tenant-db-{slug}";
    }
}
