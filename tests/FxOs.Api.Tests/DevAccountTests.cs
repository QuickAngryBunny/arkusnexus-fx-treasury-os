using System.Net;
using System.Net.Http.Json;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FxOs.Api.Tests;

/// <summary>Sprint 2 step 7: Migrator dev-user, sign-in for dummy tenants only.</summary>
[Collection(PlatformCollection.Name)]
public sealed class DevAccountTests(PlatformFixture fx)
{
    private DevAccountService Service => fx.Services.GetRequiredService<DevAccountService>();

    [Fact]
    public async Task A_dev_account_can_sign_in_and_setting_it_again_changes_the_password()
    {
        var tenant = await fx.ProvisionAsync("devuser");
        var email = $"dev_{Guid.NewGuid():N}@example.test";

        var created = await Service.SetAsync(tenant.Slug, email, "Dana Dev", "approver", "Tipo-de-cambio-2026!");
        Assert.True(created.Ok, created.Message);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(email, "Tipo-de-cambio-2026!")).StatusCode);

        var again = await Service.SetAsync(tenant.Slug, email, "Dana Dev", "admin", "Otra-clave-2027?");
        Assert.True(again.Ok, again.Message);
        Assert.Equal(created.UserId, again.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(email, "Tipo-de-cambio-2026!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(email, "Otra-clave-2027?")).StatusCode);
    }

    [Fact]
    public async Task Set_by_reset_leaves_no_known_password_and_the_reset_link_sets_it()
    {
        var tenant = await fx.ProvisionAsync("devreset");
        var email = $"boss_{Guid.NewGuid():N}@example.test";

        var created = await Service.SetAsync(tenant.Slug, email, "Bea Boss", "admin", password: null, platformAdmin: true);
        Assert.True(created.Ok, created.Message);
        Assert.Contains("Forgot password", created.Message, StringComparison.Ordinal);

        await using (var conn = await fx.Services.GetRequiredService<FxOs.Platform.Data.ControlPlaneDb>().OpenAsync())
        {
            Assert.True(await Dapper.SqlMapper.ExecuteScalarAsync<bool>(conn,
                "select is_platform_admin from user_account where id = @id", new { id = created.UserId }));
        }

        // Forgot password works for this account (it has a hash, just not one anybody knows).
        var forgot = await fx.Client().PostAsJsonAsync("/api/auth/password/forgot", new { email, locale = "en-US" });
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        await using var check = await fx.Services.GetRequiredService<FxOs.Platform.Data.ControlPlaneDb>().OpenAsync();
        Assert.Equal(1L, await Dapper.SqlMapper.ExecuteScalarAsync<long>(check,
            "select count(*) from email_outbox where to_email = @email and template = 'password_reset'", new { email }));
    }

    [Fact]
    public async Task Real_data_tenants_are_refused()
    {
        var slug = $"real_{Guid.NewGuid():N}"[..14];
        await fx.Services.GetRequiredService<TenantProvisioner>().ProvisionAsync(slug, "Real company", isDummy: false);

        var result = await Service.SetAsync(slug, $"x_{Guid.NewGuid():N}@example.test", "Real Person", "admin", "Tipo-de-cambio-2026!");
        Assert.False(result.Ok);
        Assert.Contains("real data", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short1!", "min_length")]
    [InlineData("Dana-is-the-best-2026", "personal_info")]
    public async Task Weak_passwords_are_refused(string password, string rule)
    {
        var tenant = await fx.ProvisionAsync("devweak");
        var result = await Service.SetAsync(tenant.Slug, $"dana_{Guid.NewGuid():N}@example.test", "Dana Dev", "admin", password);
        Assert.False(result.Ok);
        Assert.Contains(rule, result.Message, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> SignInAsync(string email, string password) =>
        fx.Client().PostAsJsonAsync("/api/auth/sign-in", new { email, password, remember = false });
}
