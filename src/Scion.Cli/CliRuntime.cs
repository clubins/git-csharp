using System.Net;

namespace Scion.Cli;

/// <summary>A failure the CLI reports as a one-line message and exit code 1, not a stack trace.</summary>
internal sealed class ScionCliException(string message) : Exception(message);

/// <summary>A server request that failed. The status code rides along for tests and callers that branch on it.</summary>
internal sealed class ScionServerException(HttpStatusCode statusCode, string message) : Exception(message)
{
    /// <summary>The HTTP status the server answered with.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// The execution environment commands run against: the writers they print to and, in tests, the
/// HTTP handler their server client uses (the TestServer handler). Production leaves the handler
/// unset and the client connects over the network.
/// </summary>
internal sealed record CliContext(TextWriter Out, TextWriter Err, HttpMessageHandler? HttpHandler = null);

/// <summary>
/// Wraps command actions: expected failures surface as one line on stderr with exit code 1. The
/// guard lives INSIDE the action because System.CommandLine intercepts exceptions thrown through
/// the action boundary and reports them on the console — outside the writers the tests capture.
/// </summary>
internal static class CliGuard
{
    public static int Run(Func<int> action, CliContext context)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is ScionCliException or ScionServerException)
        {
            context.Err.WriteLine($"scn: {exception.Message}");
            return 1;
        }
    }

    public static async Task<int> RunAsync(Func<Task<int>> action, CliContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ScionCliException or ScionServerException)
        {
            context.Err.WriteLine($"scn: {exception.Message}");
            return 1;
        }
    }
}
