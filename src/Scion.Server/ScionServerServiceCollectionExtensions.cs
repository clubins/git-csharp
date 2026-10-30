using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Scion.Server.Auth;
using Scion.Server.Errors;

namespace Scion.Server;

/// <summary>Registration for the server's cross-cutting services; <see cref="Program"/> composes the host from this.</summary>
public static class ScionServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the server's cross-cutting services: the bearer-token options (validated at
    /// startup, so the server fails closed rather than starting unauthenticated) and the
    /// exception-to-HTTP mapping that gives every failure a stable wire shape (spec C7), with
    /// the problem-details writer turning anything unhandled into a detail-free 500.
    /// </summary>
    public static IServiceCollection AddScionServer(this IServiceCollection services)
    {
        services.AddOptions<ScionAuthOptions>()
            .BindConfiguration(ScionAuthOptions.SectionName)
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.Token),
                "Scion:AuthToken must be configured: the server refuses to start without a bearer token.")
            .ValidateOnStart();

        services.AddExceptionHandler<ScionExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }
}
