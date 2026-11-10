using Npgsql;

namespace FxOs.Platform.Data;

/// <summary>
/// Server-level connection strings (admin credentials, no database) keyed by name, from the
/// <c>TenantServers</c> configuration section. Tenant routing stores only the key.
/// </summary>
public sealed class TenantServerRegistry(IReadOnlyDictionary<string, string> servers)
{
    public const string DefaultKey = "default";

    public string AdminConnectionString(string serverKey, string database) =>
        new NpgsqlConnectionStringBuilder(Base(serverKey)) { Database = database }.ConnectionString;

    public string TenantConnectionString(string serverKey, string database, string loginRole, string password) =>
        new NpgsqlConnectionStringBuilder(Base(serverKey))
        {
            Database = database,
            Username = loginRole,
            Password = password,
            ApplicationName = "fxos-tenant",
        }.ConnectionString;

    public async Task<NpgsqlConnection> OpenAdminAsync(string serverKey, string database, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(AdminConnectionString(serverKey, database));
        await conn.OpenAsync(ct);
        return conn;
    }

    private string Base(string serverKey) =>
        servers.TryGetValue(serverKey, out var cs)
            ? cs
            : throw new InvalidOperationException($"No tenant server '{serverKey}' is configured under TenantServers.");
}
