using FxOs.Platform;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Usage:
//   dotnet run --project src/FxOs.Migrator -- control                   migrate the control-plane database
//   dotnet run --project src/FxOs.Migrator -- tenants                   migrate every tenant database
//   dotnet run --project src/FxOs.Migrator -- provision <slug> "<name>" create or repair a dummy tenant
//   dotnet run --project src/FxOs.Migrator -- list                      list tenants and schema versions

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddFxPlatform(builder.Configuration, builder.Environment);

using var host = builder.Build();
var services = host.Services;
var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (command)
{
    case "control":
    {
        var version = await services.GetRequiredService<TenantMigrationService>().MigrateControlPlaneAsync(cts.Token);
        Console.WriteLine($"Control plane at schema v{version}.");
        return 0;
    }

    case "tenants":
    {
        var migrations = services.GetRequiredService<TenantMigrationService>();
        await migrations.MigrateControlPlaneAsync(cts.Token);
        var failures = await migrations.MigrateAllTenantsAsync(cts.Token);
        Console.WriteLine(failures == 0 ? "All tenant databases migrated." : $"{failures} tenant database(s) failed. See log.");
        return failures == 0 ? 0 : 1;
    }

    case "provision":
    {
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        if (positional.Length < 3)
        {
            Console.Error.WriteLine("Usage: provision <slug> \"<display name>\"");
            return 2;
        }

        await services.GetRequiredService<TenantMigrationService>().MigrateControlPlaneAsync(cts.Token);
        var tenant = await services.GetRequiredService<TenantProvisioner>()
            .ProvisionAsync(positional[1], positional[2], isDummy: true, cts.Token);
        Console.WriteLine($"Tenant {tenant.Slug} is {tenant.Status}: database {tenant.DatabaseName}, schema v{tenant.SchemaVersion}.");
        return 0;
    }

    case "list":
    {
        foreach (var t in await services.GetRequiredService<ControlPlaneStore>().ListAsync(cts.Token))
        {
            Console.WriteLine($"{t.Slug,-24} {t.Status,-13} dummy={t.IsDummy,-5} db={t.DatabaseName ?? "-",-28} v{t.SchemaVersion?.ToString() ?? "-"}");
        }

        return 0;
    }

    default:
        Console.Error.WriteLine("Commands: control | tenants | provision <slug> \"<name>\" | list");
        return 2;
}
