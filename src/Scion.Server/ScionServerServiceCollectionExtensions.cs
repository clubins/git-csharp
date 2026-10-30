using Microsoft.AspNetCore.Diagnostics;
using Scion.Server.Errors;

namespace Scion.Server;

/// <summary>Registration for the server's cross-cutting services; <see cref="Program"/> composes the host from this.</summary>
public static class ScionServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the server's cross-cutting services: the exception-to-HTTP mapping that gives
    /// every failure a stable wire shape (spec C7), and the problem-details writer that turns
    /// anything unhandled into a detail-free 500.
    /// </summary>
    public static IServiceCollection AddScionServer(this IServiceCollection services)
    {
        services.AddExceptionHandler<ScionExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }
}
