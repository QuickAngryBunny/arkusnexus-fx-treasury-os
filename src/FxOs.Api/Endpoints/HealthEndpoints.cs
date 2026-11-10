using FxOs.Platform.Data;

namespace FxOs.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (ControlPlaneDb controlPlane, CancellationToken ct) =>
        {
            try
            {
                await using var conn = await controlPlane.OpenAsync(ct);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Control plane unreachable");
            }
        });

        return app;
    }
}
