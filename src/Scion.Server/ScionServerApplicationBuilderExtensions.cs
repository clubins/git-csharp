namespace Scion.Server;

/// <summary>Middleware installation for the server; <see cref="Program"/> composes the pipeline from this.</summary>
public static class ScionServerApplicationBuilderExtensions
{
    /// <summary>
    /// Installs the server's middleware, outermost first: the exception handler sees failures
    /// from everything registered after it, so no endpoint can bypass the error contract.
    /// </summary>
    public static WebApplication UseScionServer(this WebApplication app)
    {
        app.UseExceptionHandler();
        return app;
    }
}
