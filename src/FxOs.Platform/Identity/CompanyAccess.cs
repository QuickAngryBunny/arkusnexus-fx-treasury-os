using Dapper;
using FxOs.Platform.Data;
using Npgsql;

namespace FxOs.Platform.Identity;

/// <summary>A company a person can open in the FX app (tenant menu, A-05 / A-06).</summary>
public sealed record CompanyChoice(Guid TenantId, string Slug, string DisplayName, bool IsDummy, string Role, bool ViaPlatformAdmin);

/// <summary>
/// Which companies a person may work in: every active company where they have an active membership, plus — for
/// platform admins — every active dummy company, as its admin. Real-data companies always need a membership.
/// </summary>
public sealed class CompanyAccess(ControlPlaneDb controlPlane)
{
    private const string Choices = """
        select t.id as tenant_id, t.slug, t.display_name, t.is_dummy, m.role, false as via_platform_admin,
               (select max(s.created_at) from session s where s.user_id = @userId and s.tenant_id = t.id) as last_used,
               m.created_at as since
        from tenant_membership m
        join tenant t on t.id = m.tenant_id and t.status = 'active'
        where m.user_id = @userId and m.status = 'active'
        union all
        select t.id, t.slug, t.display_name, t.is_dummy, 'admin', true,
               (select max(s.created_at) from session s where s.user_id = @userId and s.tenant_id = t.id),
               t.created_at
        from tenant t
        join user_account u on u.id = @userId and u.is_platform_admin
        where t.status = 'active' and t.is_dummy
          and not exists (select 1 from tenant_membership m
                          where m.tenant_id = t.id and m.user_id = @userId and m.status = 'active')
        """;

    public async Task<IReadOnlyList<CompanyChoice>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await controlPlane.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            Choices + " order by display_name", new { userId }, cancellationToken: ct));
        return rows.Select(r => r.ToChoice()).ToList();
    }

    public async Task<CompanyChoice?> FindAsync(Guid userId, Guid tenantId, CancellationToken ct = default) =>
        (await ListAsync(userId, ct)).FirstOrDefault(c => c.TenantId == tenantId);

    /// <summary>
    /// Where sign-in lands: the company used last, else the oldest membership, else null. A dummy company a platform
    /// admin opens without a membership counts only once they have opened it, so their first sign-in lands in the portal.
    /// </summary>
    public async Task<CompanyChoice?> LandingAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "select * from (" + Choices + ") c where not via_platform_admin or last_used is not null " +
            "order by last_used desc nulls last, since limit 1",
            new { userId }, tx, cancellationToken: ct));
        return row?.ToChoice();
    }

    private sealed class Row
    {
        public Guid TenantId { get; set; }
        public string Slug { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsDummy { get; set; }
        public string Role { get; set; } = "";
        public bool ViaPlatformAdmin { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime Since { get; set; }

        public CompanyChoice ToChoice() => new(TenantId, Slug, DisplayName, IsDummy, Role, ViaPlatformAdmin);
    }
}
