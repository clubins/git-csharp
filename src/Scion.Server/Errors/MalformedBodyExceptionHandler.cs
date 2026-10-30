using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

// Disambiguate from Mvc.JsonOptions: minimal-API body binding reads the Http.Json options.
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Scion.Server.Errors;

/// <summary>
/// The contract for a request body the JSON layer cannot parse (spec C7, parity tier): syntactically
/// broken JSON, or a body that is empty where a JSON object is required. Before this handler existed,
/// the framework's binding failure escaped as an unhandled exception and surfaced as a generic 500
/// problem response — a client error wearing a server-fault status. It now answers 400 with the
/// stable <c>malformed_body</c> code, so an agent can tell "fix your payload" from "the server broke"
/// without reading prose.
/// </summary>
public sealed class MalformedBodyExceptionHandler : IExceptionHandler
{
    /// <summary>The stable machine-readable discriminator clients switch on (docs/agent-api.md, error contract).</summary>
    public const string ErrorCode = "malformed_body";

    /// <summary>Fixed detail: names the problem and the fix without echoing server internals.</summary>
    public const string ErrorDetail = "The request body could not be parsed as JSON. Send a well-formed JSON document matching the endpoint's schema (see /openapi/v1.json).";

    // Written explicitly (not via DI options) so the wire contract does not depend on how the
    // host happened to configure JSON. Web defaults: camelCase property names — the same choice
    // ScionExceptionHandler makes for its problem bodies.
    private readonly JsonSerializerOptions jsonOptions;

    public MalformedBodyExceptionHandler(IOptions<JsonOptions> jsonOptions)
    {
        this.jsonOptions = jsonOptions.Value.SerializerOptions;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Body binding surfaces two ways depending on where it fails: the framework wraps syntax
        // errors in BadHttpRequestException, and a raw JsonException escapes when the body reader
        // fails outside that wrapper. Both mean the same thing — the client's bytes are not JSON
        // this server can read — so both map to the same 400 contract.
        if (exception is not (BadHttpRequestException or JsonException))
        {
            return false;
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Malformed body",
            Detail = ErrorDetail,
        };
        problem.Extensions["code"] = ErrorCode;

        httpContext.Response.StatusCode = problem.Status.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, jsonOptions, cancellationToken);
        return true;
    }
}
