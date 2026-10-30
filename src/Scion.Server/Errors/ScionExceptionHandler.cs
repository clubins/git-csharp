using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Scion.Server.Errors;

/// <summary>
/// Maps <see cref="ScionApiException"/> subtypes to their HTTP responses. Anything else returns
/// false and falls through to the registered problem-details writer, so internal failures surface
/// as a detail-free 500.
/// </summary>
public sealed class ScionExceptionHandler(ILogger<ScionExceptionHandler> logger) : IExceptionHandler
{
    // Written explicitly (not via DI options) so the wire contract does not depend on how the
    // host happened to configure JSON. Web defaults: camelCase property names.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ScionApiException scionException)
        {
            return false;
        }

        logger.LogWarning(
            "API request failed: {ErrorCode} -> {StatusCode}: {Message}",
            scionException.ErrorCode,
            scionException.StatusCode,
            scionException.Message);

        var problem = new ProblemDetails
        {
            Status = scionException.StatusCode,
            Title = TitleFor(scionException),
            Detail = scionException.Message,
        };
        problem.Extensions["code"] = scionException.ErrorCode;

        switch (scionException)
        {
            case RefConflictException refConflict:
                problem.Extensions["ref"] = refConflict.RefName;
                problem.Extensions["currentTarget"] = refConflict.CurrentTarget;
                problem.Extensions["currentRevision"] = refConflict.CurrentRevision;
                break;
            case ScionNotFoundException notFound:
                problem.Extensions["objectType"] = notFound.ObjectType;
                problem.Extensions["objectId"] = notFound.ObjectId;
                break;
        }

        httpContext.Response.StatusCode = scionException.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(problem, JsonOptions, cancellationToken);
        return true;
    }

    private static string TitleFor(ScionApiException exception) => exception switch
    {
        ScionValidationException => "Validation failed",
        ScionNotFoundException => "Not found",
        RefConflictException => "Ref conflict",
        _ => "Request failed",
    };
}
