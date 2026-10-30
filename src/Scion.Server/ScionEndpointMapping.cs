namespace Scion.Server;

/// <summary>
/// A module that contributes endpoints to the server's route table. <see cref="Program"/> calls
/// <see cref="ScionServerEndpointExtensions.MapScionEndpoints"/> once, and registered modules are
/// discovered from DI — endpoint areas plug in without touching the composition root, and test
/// hosts contribute probe routes through the same seam.
/// </summary>
public interface IScionEndpoints
{
    /// <summary>Maps this module's endpoints onto <paramref name="endpoints"/>.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

/// <summary>Endpoint wiring for the host.</summary>
public static class ScionServerEndpointExtensions
{
    /// <summary>
    /// Maps the built-in endpoints, then every <see cref="IScionEndpoints"/> module registered in DI.
    /// </summary>
    public static void MapScionEndpoints(this WebApplication app)
    {
        // Liveness probe: static body, carries no repository data. Anonymous by design so
        // orchestrators can probe without a bearer token; every other endpoint is authenticated.
        app.MapGet("/healthz", () => Results.Ok(LivenessResponse.Ok));

        foreach (var module in app.Services.GetServices<IScionEndpoints>())
        {
            module.MapEndpoints(app);
        }
    }

    private sealed record LivenessResponse(string Status)
    {
        public static readonly LivenessResponse Ok = new("ok");
    }
}
