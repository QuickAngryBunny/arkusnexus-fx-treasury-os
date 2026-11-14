using System.Net.Http.Json;
using FxOs.Api.Tenancy;
using FxOs.Platform.Data;
using FxOs.Platform.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FxOs.Api.Tests;

/// <summary>
/// One real PostgreSQL + TimescaleDB server (the same image as docker-compose) shared by all tests.
/// Each test provisions its own uniquely named tenants, so tests never depend on each other.
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    public const string PostgresImage = "timescale/timescaledb:2.17.2-pg17";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage(PostgresImage).Build();

    public FxApiFactory Factory { get; private set; } = null!;

    public IServiceProvider Services => Factory.Services;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var server = _postgres.GetConnectionString();
        await using (var conn = new NpgsqlConnection(server))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("create database fx_control", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var controlPlane = new NpgsqlConnectionStringBuilder(server) { Database = "fx_control" }.ConnectionString;
        Factory = new FxApiFactory(controlPlane, server);
        _ = Factory.Server; // boots the API, which migrates the control plane
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public Task<TenantRecord> ProvisionAsync(string prefix, string? displayName = null)
    {
        var slug = $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        return Services.GetRequiredService<TenantProvisioner>()
            .ProvisionAsync(slug, displayName ?? $"Test {slug}", isDummy: true);
    }

    public HttpClient Client() => Factory.CreateClient();

    /// <summary>Opens the tenant's own database with its own login, as the API does.</summary>
    public async Task<NpgsqlConnection> OpenTenantAsync(TenantRecord tenant)
    {
        var password = await Services.GetRequiredService<FxOs.Platform.Secrets.ISecretStore>().GetAsync(tenant.SecretRef!);
        var cs = Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(tenant.ServerKey!, tenant.DatabaseName!, tenant.LoginRole!, password!);
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, string? tenant, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (tenant is not null)
        {
            request.Headers.Add(TenantResolutionMiddleware.HeaderName, tenant);
        }

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        return request;
    }
}

public sealed class FxApiFactory(string controlPlane, string server) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ControlPlane", controlPlane);
        builder.UseSetting("TenantServers:default", server);
        builder.UseSetting("Platform:DevTenantSwitcher:Enabled", "true");
        builder.UseSetting("Platform:MigrateControlPlaneOnStartup", "true");
    }
}

[CollectionDefinition(Name)]
public sealed class PlatformCollection : ICollectionFixture<PlatformFixture>
{
    public const string Name = "platform";
}
