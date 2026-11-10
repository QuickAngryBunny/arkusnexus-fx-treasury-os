using System.Security.Cryptography;
using FxOs.Platform.Data;

namespace FxOs.Platform.Secrets;

/// <summary>Stores tenant database credentials. Azure Key Vault implements this before any non-internal environment.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string secretRef, CancellationToken ct = default);

    Task SetAsync(string secretRef, string value, CancellationToken ct = default);
}

/// <summary>
/// DEVELOPMENT ONLY: keeps secrets in the control-plane <c>dev_secret</c> table.
/// Registration refuses this store outside Development, Testing and Internal environments.
/// </summary>
public sealed class DevSecretStore(ControlPlaneDb controlPlane) : ISecretStore
{
    public async Task<string?> GetAsync(string secretRef, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.ScalarAsync<string>(
            "select secret_value from dev_secret where secret_ref = @secretRef", new { secretRef }, ct: ct);
    }

    public async Task SetAsync(string secretRef, string value, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        await conn.ExecAsync(
            """
            insert into dev_secret (secret_ref, secret_value) values (@secretRef, @value)
            on conflict (secret_ref) do update set secret_value = excluded.secret_value
            """,
            new { secretRef, value },
            ct: ct);
    }
}

internal static class Passwords
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Alphanumeric only, so it is safe inside a SQL string literal in CREATE ROLE.</summary>
    public static string Generate(int length = 40) => RandomNumberGenerator.GetString(Alphabet, length);
}
