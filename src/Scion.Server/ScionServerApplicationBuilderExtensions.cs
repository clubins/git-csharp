using Scion.Server.Auth;

namespace Scion.Server;

/// <summary>Middleware installation for the server; <see cref="Program"/> composes the pipeline from this.</summary>
public static class ScionServerApplicationBuilderExtensions
{
    /// <summary>
    /// Installs the server's middleware, outermost first: the exception handler sees failures
    /// from everything registered after it, so no endpoint can bypass the error contract, and
    /// the auth gate runs in front of every endpoint that is not marked anonymous.
    /// </summary>
    public static WebApplication UseScionServer(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseMiddleware<BearerAuthMiddleware>();
        return app;
    }
}
