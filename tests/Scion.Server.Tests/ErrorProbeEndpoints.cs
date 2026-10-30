using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scion.Server.Errors;

namespace Scion.Server.Tests;

/// <summary>
/// Probe endpoints that throw each <see cref="ScionApiException"/> subtype through the real
/// pipeline, so error-shape tests assert the exact C7 wire contract. Registered only by
/// <see cref="ScionServerFactory"/> — never in production.
/// </summary>
internal sealed class ErrorProbeEndpoints : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/test/validation", () => Throw(new ScionValidationException("Message is required.")));
        endpoints.MapGet("/test/not-found", () => Throw(new ScionNotFoundException("changeset", "sha256:abc")));
        endpoints.MapGet("/test/ref-conflict", () => Throw(new RefConflictException("main", "sha256:def", 7)));
    }

    private static IResult Throw(ScionApiException exception) => throw exception;
}
