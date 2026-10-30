using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Scion.Server.Auth;

/// <summary>
/// Bearer-token gate: every endpoint without <see cref="IAllowAnonymous"/> metadata requires
/// `Authorization: Bearer &lt;token&gt;`. The token is compared in constant time so response
/// timing does not leak how much of the secret matched.
/// </summary>
public sealed class BearerAuthMiddleware(RequestDelegate next, IOptionsMonitor<ScionAuthOptions> options)
{
    private const string Scheme = "Bearer";

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var token = GetBearerToken(context.Request);
        if (token is null || !TokenMatches(token, options.CurrentValue.Token))
        {
            await RejectAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>Extracts the token from "Bearer &lt;token&gt;", tolerating scheme case and whitespace; null when the header is absent or malformed.</summary>
    private static string? GetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.Length <= Scheme.Length || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[Scheme.Length..].TrimStart();
        return token.Length == 0 ? null : token;
    }

    private static bool TokenMatches(string candidate, string expected)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return candidateBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = Scheme;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "A valid bearer token is required; send 'Authorization: Bearer <token>'.",
            Extensions = { ["code"] = "unauthorized" },
        });
    }
}
