using Dapper;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FxOs.Api.Tests;

/// <summary>Keeping a tested company: a dummy tenant becomes a real one (one way), and the Migrator deletes dummy ones.</summary>
[Collection(PlatformCollection.Name)]
public sealed class MarkRealDataTests(PlatformFixture fx)
{
    [Fact]
    public async Task A_dummy_tenant_becomes_real_only_without_dummy_profiles_and_then_cannot_be_deleted()
    {
        var tenant = await fx.ProvisionAsync("mreal");
        var (memberId, _, _) = await fx.SignInAsync(tenant, "admin");
        var admin = fx.Services.GetRequiredService<TenantAdminService>();
        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            await conn.ExecuteAsync("insert into user_profile (user_id, email, display_name) values (@m, 'me@example.test', 'Me'), (gen_random_uuid(), 'dummy@example.test', 'Dummy')",
                new { m = memberId });
        }

        Assert.Equal("dummy_data", (await admin.MarkRealDataAsync(tenant.Id, ControlActor.System)).Code);

        await using (var conn = await fx.OpenTenantAsync(tenant))
        {
            await conn.ExecuteAsync("delete from user_profile where email = 'dummy@example.test'");
        }

        Assert.True((await admin.MarkRealDataAsync(tenant.Id, ControlActor.System)).Ok);
        Assert.Equal("already_real", (await admin.MarkRealDataAsync(tenant.Id, ControlActor.System)).Code);
        var stored = await fx.Services.GetRequiredService<ControlPlaneStore>().FindByIdAsync(tenant.Id);
        Assert.False(stored!.IsDummy);
        Assert.Equal("real_data", (await admin.DeleteAsync(tenant.Id, stored.DisplayName, ControlActor.System)).Code);
        Assert.Equal("real_data", (await admin.RequestDummyDataAsync(tenant.Id, load: true, memberId)).Code);
    }

    [Fact]
    public async Task The_migrator_path_deletes_a_dummy_tenant_as_the_system()
    {
        var tenant = await fx.ProvisionAsync("mdel");
        var admin = fx.Services.GetRequiredService<TenantAdminService>();

        Assert.Equal("name_mismatch", (await admin.DeleteAsync(tenant.Id, "wrong", ControlActor.System)).Code);
        Assert.True((await admin.DeleteAsync(tenant.Id, tenant.DisplayName, ControlActor.System)).Ok);

        var stored = await fx.Services.GetRequiredService<ControlPlaneStore>().FindByIdAsync(tenant.Id);
        Assert.Equal(TenantStatus.Deleted, stored!.Status);
        await using var cp = await fx.Services.GetRequiredService<FxOs.Platform.Data.ControlPlaneDb>().OpenAsync();
        Assert.Equal("system", await cp.ExecuteScalarAsync<string>(
            "select actor_kind from control_audit_log where tenant_id = @id and action = 'tenant.deleted'", new { id = tenant.Id }));
    }
}
