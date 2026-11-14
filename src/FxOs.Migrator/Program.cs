using FxOs.Platform;
using FxOs.Platform.Identity;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Usage:
//   dotnet run --project src/FxOs.Migrator -- control                   migrate the control-plane database
//   dotnet run --project src/FxOs.Migrator -- tenants                   migrate every tenant database
//   dotnet run --project src/FxOs.Migrator -- provision <slug> "<name>" create or repair a dummy tenant
//   dotnet run --project src/FxOs.Migrator -- list                      list tenants and schema versions
//   dotnet run --project src/FxOs.Migrator -- dev-user <slug> <email> "<name>" [admin|approver|standard]
//                                              [--platform-admin] [--set-by-reset]
//                                                                       sign-in for a dummy tenant; asks for the password,
//                                                                       or with --set-by-reset you set it via "Forgot password?"

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

    case "dev-user":
    {
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        if (positional.Length < 4)
        {
            Console.Error.WriteLine("Usage: dev-user <slug> <email> \"<name>\" [admin|approver|standard] [--platform-admin] [--set-by-reset]");
            return 2;
        }

        // Typed (hidden) or piped; never an argument, so it stays out of shell history.
        var setByReset = args.Contains("--set-by-reset");
        var password = setByReset ? null : ReadPassword("Password (12+ characters, a number and a symbol): ");
        var result = await services.GetRequiredService<DevAccountService>().SetAsync(
            positional[1], positional[2], positional[3], positional.Length > 4 ? positional[4] : MembershipRole.Admin, password,
            platformAdmin: args.Contains("--platform-admin"), cts.Token);
        (result.Ok ? Console.Out : Console.Error).WriteLine(result.Message);
        return result.Ok ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("Commands: control | tenants | provision <slug> \"<name>\" | list | dev-user <slug> <email> \"<name>\" [role]");
        return 2;
}

static string ReadPassword(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    Console.Write(prompt);
    var chars = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Length > 0)
            {
                chars.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            chars.Append(key.KeyChar);
        }
    }

    Console.WriteLine();
    return chars.ToString();
}
