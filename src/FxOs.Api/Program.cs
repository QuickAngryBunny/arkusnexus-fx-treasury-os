using FxOs.Api.Endpoints;
using FxOs.Api.Tenancy;
using FxOs.Platform;
using FxOs.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFxPlatform(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();

var app = builder.Build();
var settings = app.Services.GetRequiredService<PlatformSettings>();

if (settings.MigrateControlPlaneOnStartup)
{
    await app.Services.GetRequiredService<TenantMigrationService>().MigrateControlPlaneAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapHealthEndpoints();
if (settings.DevTenantSwitcherEnabled)
{
    app.MapDevEndpoints();
}

app.MapTenantEndpoints();

await app.RunAsync();

public partial class Program;
