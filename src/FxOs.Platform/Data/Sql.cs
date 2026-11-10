using Dapper;
using Npgsql;

namespace FxOs.Platform.Data;

internal static class Sql
{
    /// <summary>Quotes a PostgreSQL identifier. Callers still validate names first (see TenantSlug).</summary>
    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static Task<int> ExecAsync(
        this NpgsqlConnection conn, string sql, object? args = null, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));

    public static Task<T?> ScalarAsync<T>(
        this NpgsqlConnection conn, string sql, object? args = null, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        conn.ExecuteScalarAsync<T>(new CommandDefinition(sql, args, tx, cancellationToken: ct));
}
