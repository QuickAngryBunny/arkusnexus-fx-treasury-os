using Dapper;
using FxOs.Platform.Audit;
using FxOs.Platform.Data;
using FxOs.Platform.Email;
using FxOs.Platform.Identity;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FxOs.Platform;

public sealed record PlatformSettings(bool DevTenantSwitcherEnabled, bool MigrateControlPlaneOnStartup);

public static class PlatformServiceCollectionExtensions
{
    /// <summary>Environments that may run with dummy data, the dev secret store and the developer tenant switcher.</summary>
    public static bool IsInternalEnvironment(this IHostEnvironment env) =>
        env.IsDevelopment() || env.IsEnvironment("Testing") || env.IsEnvironment("Internal");

    public static IServiceCollection AddFxPlatform(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var controlPlane = configuration.GetConnectionString("ControlPlane");
        if (string.IsNullOrWhiteSpace(controlPlane))
        {
            throw new InvalidOperationException("ConnectionStrings:ControlPlane is required.");
        }

        var servers = configuration.GetSection("TenantServers").GetChildren()
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .ToDictionary(s => s.Key, s => s.Value!, StringComparer.Ordinal);
        if (!servers.ContainsKey(TenantServerRegistry.DefaultKey))
        {
            throw new InvalidOperationException("TenantServers:default is required (server-level admin connection string).");
        }

        var settings = new PlatformSettings(
            DevTenantSwitcherEnabled: configuration.GetValue("Platform:DevTenantSwitcher:Enabled", false),
            MigrateControlPlaneOnStartup: configuration.GetValue("Platform:MigrateControlPlaneOnStartup", false));

        if (!environment.IsInternalEnvironment())
        {
            // Phase 0 has no sign-in and only the dev secret store; nothing here may run outside internal environments.
            throw new InvalidOperationException(
                $"Environment '{environment.EnvironmentName}' is not internal. Phase 0 runs only in Development, Testing or Internal.");
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton(settings);
        services.AddSingleton(_ => new ControlPlaneDb(controlPlane));
        services.AddSingleton(new TenantServerRegistry(servers));
        services.AddSingleton<ISecretStore, DevSecretStore>();
        services.AddSingleton<ControlPlaneStore>();
        services.AddSingleton<TenantDataSources>();
        services.AddSingleton<TenantProvisioner>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(IdentityOptions.Default);
        services.AddSingleton<IdentityStore>();
        services.AddSingleton(SessionOptions.Default);
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SignInService>();

        // Email: outbox (encrypted bodies) -> dispatcher -> Firebase Trigger Email `mail` collection.
        var email = configuration.GetSection(EmailSettings.Section).Get<EmailSettings>() ?? new EmailSettings();
        services.AddSingleton(email);
        services.AddDataProtection().SetApplicationName("FxOs");
        services.AddSingleton<EmailOutbox>();
        services.TryAddSingleton<IMailTransport, FirestoreMailTransport>();
        services.AddSingleton<EmailDispatcher>();
        if (email.Dispatcher.Enabled && email.Firebase.IsConfigured)
        {
            services.AddHostedService<EmailDispatcherService>();
        }
        services.AddSingleton<TenantMigrationService>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantConnectionFactory, TenantConnectionFactory>();
        services.AddScoped<IAuditWriter, AuditWriter>();

        services.AddMemoryCache();
        return services;
    }
}
