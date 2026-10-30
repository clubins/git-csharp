using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Enums cross the wire as lowercase names, not numbers — a diff entry says "added", not 0 —
        // so agent-side payloads and logs stay readable and stable under enum reorderings.
        services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        services.AddOptions<ScionAuthOptions>()
            .BindConfiguration(ScionAuthOptions.SectionName)
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.Token),
                "Scion:AuthToken must be configured: the server refuses to start without a bearer token.")
            .ValidateOnStart();

        services.AddExceptionHandler<MalformedBodyExceptionHandler>();
        services.AddExceptionHandler<ScionExceptionHandler>();
        services.AddProblemDetails();

        // The machine-readable API contract (spec D5): generated from the same route metadata the
        // server serves, so the document cannot drift from the routes. Transformers below add the
        // pieces route metadata cannot express — the bearer scheme, the 401 it implies, and the
        // lowercase enum values the wire actually carries.
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(ScionOpenApi.AddBearerScheme);
            options.AddOperationTransformer(ScionOpenApi.AddAuthRequirements);
            options.AddSchemaTransformer(ScionOpenApi.LowercaseEnumValues);
        });

        return services;
    }
}
