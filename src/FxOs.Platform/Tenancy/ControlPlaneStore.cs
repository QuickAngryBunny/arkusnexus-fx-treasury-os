using Dapper;
using FxOs.Platform.Data;

namespace FxOs.Platform.Tenancy;

/// <summary>Read access to tenants and their database routing in the control plane.</summary>
public sealed class ControlPlaneStore(ControlPlaneDb controlPlane)
{
    private const string SelectTenant = """
        select t.id, t.slug, t.display_name, t.status, t.is_dummy,
               d.server_key, d.database_name, d.login_role, d.secret_ref, d.schema_version
        from tenant t
        left join tenant_database d on d.tenant_id = t.id
        """;

    public async Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantRecord>(
            new CommandDefinition(SelectTenant + " where t.slug = @slug", new { slug }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var rows = await conn.QueryAsync<TenantRecord>(
            new CommandDefinition(SelectTenant + " order by t.slug", cancellationToken: ct));
        return rows.AsList();
    }
}
