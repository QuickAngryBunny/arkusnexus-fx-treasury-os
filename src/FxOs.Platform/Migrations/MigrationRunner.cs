using System.Text.RegularExpressions;
using Dapper;
using FxOs.Platform.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FxOs.Platform.Migrations;

/// <summary>
/// Applies pending migrations of one set to one database. Each migration runs in its own transaction,
/// an advisory lock stops two runners from racing, and a changed file that was already applied is refused.
/// On a server without TimescaleDB (Cloud SQL for PostgreSQL), the two TimescaleDB statements early tenant
/// migrations use are skipped, so those tables stay plain tables; the checksum is still the file's own.
/// New migrations must be plain PostgreSQL.
/// </summary>
public static partial class MigrationRunner
{
    private const long LockKey = 7_270_001;

    [GeneratedRegex(@"^\s*create extension if not exists timescaledb\s*;[^\S\n]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TimescaleExtension();

    [GeneratedRegex(@"^\s*select create_hypertable\([^;]*\)\s*;[^\S\n]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CreateHypertable();

    /// <summary>The SQL to run: unchanged with TimescaleDB, without its two statements otherwise.</summary>
    public static string ForServer(string sql, bool timescale) =>
        timescale ? sql : CreateHypertable().Replace(TimescaleExtension().Replace(sql, "-- (TimescaleDB not available: skipped)"),
            "-- (TimescaleDB not available: plain table)");

    public static async Task<int> RunAsync(NpgsqlConnection conn, string set, ILogger? log = null, CancellationToken ct = default)
    {
        var migrations = MigrationCatalog.Load(set);

        await conn.ExecAsync("""
            create table if not exists schema_migrations (
                version     int primary key,
                name        text not null,
                checksum    text not null,
                applied_at  timestamptz not null default now()
            )
            """, ct: ct);

        var timescale = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from pg_available_extensions where name = 'timescaledb')", cancellationToken: ct));

        await conn.ExecAsync("select pg_advisory_lock(@key)", new { key = LockKey }, ct: ct);
        try
        {
            var applied = (await conn.QueryAsync<(int Version, string Checksum)>(
                    new CommandDefinition("select version, checksum from schema_migrations", cancellationToken: ct)))
                .ToDictionary(row => row.Version, row => row.Checksum);

            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    if (!string.Equals(checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Migration {set}/V{migration.Version:D4}__{migration.Name} changed after it was applied to " +
                            $"database '{conn.Database}'. Never edit an applied migration; add a new one.");
                    }

                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(ForServer(migration.Sql, timescale), conn, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await conn.ExecAsync(
                    "insert into schema_migrations (version, name, checksum) values (@Version, @Name, @Checksum)",
                    new { migration.Version, migration.Name, migration.Checksum },
                    tx,
                    ct);
                await tx.CommitAsync(ct);

                log?.LogInformation(
                    "Applied migration {Set}/V{Version:D4}__{Name} to {Database}",
                    set, migration.Version, migration.Name, conn.Database);
            }

            return migrations[^1].Version;
        }
        finally
        {
            await conn.ExecAsync("select pg_advisory_unlock(@key)", new { key = LockKey }, ct: CancellationToken.None);
        }
    }
}
