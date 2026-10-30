using Microsoft.AspNetCore.Authorization;

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
        app.MapGet("/healthz", () => Results.Ok(LivenessResponse.Ok))
        .WithTags("health")
        .WithSummary("Liveness probe")
        .WithDescription("Static body, carries no repository data. Anonymous: orchestrators probe without a bearer token.")
        .Produces(StatusCodes.Status200OK, contentType: "application/json")
        .AllowAnonymous();

        // The machine-readable API contract (spec D5), served as /openapi/v1.json. Anonymous like
        // /healthz: the document describes the public contract and carries no repository data —
        // agents and CI fetch it to discover the API before they hold a token.
        app.MapOpenApi().AllowAnonymous();

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
