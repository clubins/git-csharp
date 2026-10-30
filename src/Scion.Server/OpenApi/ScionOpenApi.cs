using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace Scion.Server;

/// <summary>
/// OpenAPI document enrichments the route metadata cannot express. The generated document comes
/// from the same route table the server serves; these transformers add only what middleware-level
/// behavior looks like on the wire: the bearer scheme (the auth gate is custom middleware, invisible
/// to the generator), the 401 every authenticated operation can answer, and the lowercase enum
/// values the JSON layer actually emits (<see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// with camelCase naming) — without the rewrite the schema would advertise PascalCase names the
/// wire never sends.
/// </summary>
public static class ScionOpenApi
{
    /// <summary>The security-scheme key; operation requirements reference the scheme by this id.</summary>
    public const string BearerSchemeId = "Bearer";

    /// <summary>The component schema id the generator assigns to <see cref="ProblemDetails"/> bodies.</summary>
    public const string ProblemDetailsSchemaId = "ProblemDetails";

    /// <summary>
    /// Declares the bearer scheme in the document's components. The scheme is HTTP bearer —
    /// the same token Scion:AuthToken configures, sent as <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    public static Task AddBearerScheme(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerSchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "The repository's configured auth token (Scion:AuthToken), sent as 'Authorization: Bearer <token>'.",
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds the security requirement and the 401 response to every operation the bearer middleware
    /// actually gates — everything without <see cref="IAllowAnonymous"/> metadata (that is
    /// <c>/healthz</c> and the OpenAPI document itself). The middleware is the authority; the
    /// document follows it rather than the reverse.
    /// </summary>
    public static Task AddAuthRequirements(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        // The host document must travel with the reference: without it the JSON writer cannot
        // resolve the scheme key and serializes the requirement as an empty object.
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(BearerSchemeId, context.Document)] = [],
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["401"] = new OpenApiResponse
        {
            Description = "Missing, malformed, or wrong bearer token. The body is the standard error problem (code 'unauthorized').",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                // The generated document carries the ProblemDetails component (the same schema the
                // 400/409 producers reference); the 401 body is that same problem shape.
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchemaReference(ProblemDetailsSchemaId),
                },
            },
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites enum schema values from the CLR member names the generator emits ("Added") to the
    /// camelCase names the wire carries ("added") — the JSON layer's
    /// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> is configured with
    /// camelCase naming, and the schema must match the payloads clients receive.
    /// </summary>
    public static Task LowercaseEnumValues(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Enum is not { Count: > 0 } values)
        {
            return Task.CompletedTask;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is JsonValue value && value.TryGetValue<string>(out var name) && name.Length > 0)
            {
                values[i] = JsonValue.Create(CamelCase(name));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>CamelCases a CLR enum member name the way the JSON naming policy does ("Added" -> "added").</summary>
    private static string CamelCase(string name)
    {
        return string.Create(name.Length, name, static (span, source) =>
        {
            source.CopyTo(span);
            span[0] = char.ToLowerInvariant(span[0]);
        });
    }
}
