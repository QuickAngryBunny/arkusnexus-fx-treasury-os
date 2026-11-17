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

        // Where tenant database passwords live: "dev" (control-plane table, internal environments only) or "google"
        // (Secret Manager, Secrets:Google:ProjectId). Any other environment needs Secret Manager and no dev switcher.
        var secretProvider = configuration.GetValue("Secrets:Provider", "dev")!;
        if (!environment.IsInternalEnvironment())
        {
            if (secretProvider != "google")
            {
                throw new InvalidOperationException(
                    $"Environment '{environment.EnvironmentName}' is not internal: set Secrets:Provider to \"google\" (Secret Manager). " +
                    "The dev secret store runs only in Development, Testing or Internal.");
            }

            if (settings.DevTenantSwitcherEnabled)
            {
                throw new InvalidOperationException(
                    $"Environment '{environment.EnvironmentName}' is not internal: Platform:DevTenantSwitcher:Enabled must be false.");
            }
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton(settings);
        services.AddSingleton(_ => new ControlPlaneDb(controlPlane));
        services.AddSingleton(new TenantServerRegistry(servers));
        if (secretProvider == "google")
        {
            var projectId = configuration["Secrets:Google:ProjectId"];
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new InvalidOperationException("Secrets:Google:ProjectId is required with Secrets:Provider \"google\".");
            }

            services.AddSingleton<ISecretStore>(_ => new GoogleSecretManagerStore(
                Google.Cloud.SecretManager.V1.SecretManagerServiceClient.Create(), projectId));
        }
        else if (secretProvider == "dev")
        {
            services.AddSingleton<ISecretStore, DevSecretStore>();
        }
        else
        {
            throw new InvalidOperationException($"Secrets:Provider '{secretProvider}' is unknown (dev or google).");
        }
        services.AddSingleton<ControlPlaneStore>();
        services.AddSingleton<TenantDataSources>();
        services.AddSingleton<TenantProvisioner>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(IdentityOptions.Default);
        services.AddSingleton<IdentityStore>();
        services.AddSingleton(SessionOptions.Default);
        services.AddSingleton<SessionStore>();
        services.AddSingleton<CompanyAccess>();
        services.AddSingleton<SignInService>();
        services.AddSingleton<EmailClocks>();
        services.AddSingleton<FxOs.Platform.Audit.ControlAuditMirror>();
        services.AddSingleton<FxOs.Platform.Alerts.RateAlertChecker>();
        services.AddSingleton<PasswordResetService>();
        services.AddSingleton<DevAccountService>();
        services.AddSingleton<TenantAdminService>();
        services.AddSingleton<InvitationFlow>();
        services.AddSingleton<TeamService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<TenantSetupRunner>();

        // The web app's address, for links in emails. Always ends with "/" so relative paths append.
        var publicBase = configuration["App:PublicBaseUrl"] ?? "http://localhost:3000/";
        services.AddSingleton(new AppLinks(new Uri(publicBase.EndsWith('/') ? publicBase : publicBase + "/")));

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
