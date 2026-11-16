using Dapper;
using FxOs.Platform.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FxOs.Api.Tests;

/// <summary>
/// Hosting on Firebase / Google Cloud means Cloud SQL for PostgreSQL, which has no TimescaleDB. Every control-plane
/// and tenant migration must run on plain PostgreSQL 17, and the rate tables come out as ordinary tables.
/// </summary>
public sealed class CloudSqlCompatibilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Every_migration_runs_on_plain_postgresql()
    {
        var server = _postgres.GetConnectionString();
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            Assert.False(await admin.ExecuteScalarAsync<bool>(
                "select exists (select 1 from pg_available_extensions where name = 'timescaledb')"));
            await admin.ExecuteAsync("create database cp; create database tn;");
        }

        await using var cp = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "cp" }.ConnectionString);
        await cp.OpenAsync();
        Assert.Equal(MigrationCatalog.Load(MigrationCatalog.ControlPlane)[^1].Version,
            await MigrationRunner.RunAsync(cp, MigrationCatalog.ControlPlane));

        await using var tn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "tn" }.ConnectionString);
        await tn.OpenAsync();
        Assert.Equal(MigrationCatalog.Load(MigrationCatalog.Tenant)[^1].Version,
            await MigrationRunner.RunAsync(tn, MigrationCatalog.Tenant));

        // Applied with the files' own checksums, so a second run (or a later move to TimescaleDB) finds nothing changed.
        await MigrationRunner.RunAsync(tn, MigrationCatalog.Tenant);
        Assert.Equal(2L, await tn.ExecuteScalarAsync<long>(
            "select count(*) from pg_tables where schemaname = 'public' and tablename in ('fx_rate', 'interest_rate')"));
    }

    [Fact]
    public void Only_the_two_timescaledb_statements_are_skipped()
    {
        const string sql = """
            create extension if not exists timescaledb;
            create table t (d date primary key);
            select create_hypertable('t', by_range('d', interval '5 years'));
            select 1;
            """;
        var plain = MigrationRunner.ForServer(sql, timescale: false);
        Assert.DoesNotContain("create extension", plain);
        Assert.DoesNotContain("create_hypertable", plain);
        Assert.Contains("create table t", plain);
        Assert.Contains("select 1;", plain);
        Assert.Equal(sql, MigrationRunner.ForServer(sql, timescale: true));
    }
}
