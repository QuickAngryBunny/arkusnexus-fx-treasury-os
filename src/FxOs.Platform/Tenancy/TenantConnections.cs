using System.Collections.Concurrent;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using Npgsql;

namespace FxOs.Platform.Tenancy;

/// <summary>One connection pool per tenant database, created on first use.</summary>
public sealed class TenantDataSources(TenantServerRegistry servers, ISecretStore secrets) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, NpgsqlDataSource> _sources = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<NpgsqlDataSource> GetAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        if (_sources.TryGetValue(tenant.Id, out var existing))
        {
            return existing;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_sources.TryGetValue(tenant.Id, out existing))
            {
                return existing;
            }

            if (tenant.ServerKey is null || tenant.DatabaseName is null || tenant.LoginRole is null || tenant.SecretRef is null)
            {
                throw new TenantIsolationException($"Tenant '{tenant.Slug}' has no database routing yet.");
            }

            var password = await secrets.GetAsync(tenant.SecretRef, ct)
                ?? throw new InvalidOperationException($"Secret '{tenant.SecretRef}' for tenant '{tenant.Slug}' was not found.");

            var source = NpgsqlDataSource.Create(
                servers.TenantConnectionString(tenant.ServerKey, tenant.DatabaseName, tenant.LoginRole, password));
            _sources[tenant.Id] = source;
            return source;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values)
        {
            await source.DisposeAsync();
        }

        _sources.Clear();
        _gate.Dispose();
    }
}

/// <summary>Opens a connection to the current request's tenant database, and only that one.</summary>
public interface ITenantConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class TenantConnectionFactory(ITenantContext context, TenantDataSources sources) : ITenantConnectionFactory
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var tenant = context.Require();
        var source = await sources.GetAsync(tenant, ct);
        var conn = await source.OpenConnectionAsync(ct);
        try
        {
            // Defense in depth: even with routing wrong, a database that belongs to another tenant is refused.
            var owner = await conn.ScalarAsync<Guid?>("select tenant_id from tenant_settings where id = 1", ct: ct);
            if (owner != tenant.Id)
            {
                throw new TenantIsolationException(
                    $"Database '{conn.Database}' does not belong to tenant '{tenant.Slug}'. Request refused.");
            }

            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
