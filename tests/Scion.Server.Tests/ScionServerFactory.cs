using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scion.Server.Auth;
using Scion.Server.Storage;

namespace Scion.Server.Tests;

/// <summary>
/// The shared harness: hosts the real <see cref="Program"/> under <see cref="WebApplicationFactory"/>
/// so tests drive the authentic middleware, routing, and error-handling pipeline rather than a
/// test double. Configures a known bearer token and an isolated per-instance data directory for
/// the real stores (SQLite log and refs, blob files); <see cref="CreateAuthedClient"/> returns a
/// client that sends the token.
/// </summary>
public sealed class ScionServerFactory : WebApplicationFactory<Program>
{
    /// <summary>The token the harness configures; authenticated requests send this.</summary>
    public const string TestToken = "scion-test-token";

    private readonly string? configuredToken;

    /// <summary>Isolated per-instance store root: two factories never share store state.</summary>
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "scion-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Creates the harness with <see cref="TestToken"/> configured.</summary>
    public ScionServerFactory()
        : this(TestToken)
    {
    }

    /// <summary>
    /// Creates the harness with an explicit token configuration. Pass <see langword="null"/> for a
    /// host with no token at all — the fail-closed startup test's subject.
    /// </summary>
    public ScionServerFactory(string? token)
    {
        configuredToken = token;
    }

    /// <summary>An <see cref="HttpClient"/> that sends the configured bearer token on every request.</summary>
    public HttpClient CreateAuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // The token is supplied at the options layer, not via configuration: the factory's
            // IWebHostBuilder configuration callbacks did not reach the app's IConfiguration the
            // server binds from (observed: BindConfiguration saw no token), while services
            // registered here do flow. Registering after AddScionServer's BindConfiguration
            // means this configure step runs last and wins.
            if (configuredToken is not null)
            {
                services.AddSingleton<IConfigureOptions<ScionAuthOptions>>(
                    new ConfigureNamedOptions<ScionAuthOptions>(Options.DefaultName, options => options.Token = configuredToken));
            }

            // The data directory is supplied the same way and for the same reason — the server
            // refuses to start without one, and every test host gets its own isolated stores.
            services.AddSingleton<IConfigureOptions<ScionStorageOptions>>(
                new ConfigureNamedOptions<ScionStorageOptions>(Options.DefaultName, options => options.DataDirectory = dataDirectory));

            // PRESERVE: ErrorShapeTests drive the C7 mapping through these probe endpoints
            // (contributed as a DI-registered endpoint module by the test harness itself).
            services.AddSingleton<IScionEndpoints>(new ErrorProbeEndpoints());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
