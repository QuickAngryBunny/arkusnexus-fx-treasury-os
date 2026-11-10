using Npgsql;

namespace FxOs.Platform.Data;

/// <summary>Connection pool for the shared control-plane database.</summary>
public sealed class ControlPlaneDb(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => _source.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
