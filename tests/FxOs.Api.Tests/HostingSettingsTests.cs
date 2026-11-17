using FxOs.Platform;
using FxOs.Platform.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FxOs.Api.Tests;

/// <summary>Hosted (Firebase / Google Cloud) the API needs Secret Manager and no dev switcher; internally the dev store stays.</summary>
public sealed class HostingSettingsTests
{
    private static IServiceCollection Add(string environment, Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlPlane"] = "Host=localhost;Database=fx_control",
            ["TenantServers:default"] = "Host=localhost",
        };
        foreach (var (k, v) in extra) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddFxPlatform(config, new Env(environment));
        return services;
    }

    [Fact]
    public void Production_needs_secret_manager_and_no_dev_switcher()
    {
        Assert.Contains("Secrets:Provider", Assert.Throws<InvalidOperationException>(() => Add("Production", [])).Message);
        Assert.Contains("DevTenantSwitcher", Assert.Throws<InvalidOperationException>(() => Add("Production",
            new() { ["Secrets:Provider"] = "google", ["Secrets:Google:ProjectId"] = "p", ["Platform:DevTenantSwitcher:Enabled"] = "true" })).Message);
        Assert.Contains("ProjectId", Assert.Throws<InvalidOperationException>(() => Add("Production",
            new() { ["Secrets:Provider"] = "google" })).Message);

        var services = Add("Production", new() { ["Secrets:Provider"] = "google", ["Secrets:Google:ProjectId"] = "p" });
        Assert.Contains(services, d => d.ServiceType == typeof(ISecretStore) && d.ImplementationFactory is not null);
    }

    [Fact]
    public void Internal_environments_keep_the_dev_store()
    {
        var services = Add("Internal", []);
        Assert.Contains(services, d => d.ServiceType == typeof(ISecretStore) && d.ImplementationType == typeof(DevSecretStore));
        Assert.Throws<InvalidOperationException>(() => Add("Internal", new() { ["Secrets:Provider"] = "vault" }));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FxOs.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
