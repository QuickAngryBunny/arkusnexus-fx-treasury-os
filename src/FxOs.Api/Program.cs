using FxOs.Api.Endpoints;
using FxOs.Api.Tenancy;
using FxOs.Platform;
using FxOs.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFxPlatform(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
// Tenant setup (portal A-03): database and invitation steps; the Python worker copies the rate history.
if (builder.Configuration.GetValue("Platform:RunTenantSetup", true))
{
    builder.Services.AddHostedService<TenantSetupService>();
}

// Copies each company's sign-ins, invitations and role changes into its own audit log (tenant V0011).
if (builder.Configuration.GetValue("Platform:MirrorControlAudit", true))
{
    builder.Services.AddHostedService<FxOs.Platform.Audit.ControlAuditMirrorService>();
}

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
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapAccountEndpoints();
if (settings.DevTenantSwitcherEnabled)
{
    app.MapDevEndpoints();
}

app.MapTenantEndpoints();
app.MapCompanyEndpoints();
app.MapTeamEndpoints();
app.MapRateEndpoints();
app.MapForecastEndpoints();
app.MapRateHistoryEndpoints();
app.MapNewsEndpoints();

await app.RunAsync();

public partial class Program;
