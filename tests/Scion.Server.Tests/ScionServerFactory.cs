using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scion.Server;

namespace Scion.Server.Tests;

/// <summary>
/// The shared harness: hosts the real <see cref="Program"/> under <see cref="WebApplicationFactory"/>
/// so tests drive the authentic middleware, routing, and error-handling pipeline rather than a
/// test double. Configures a known bearer token and contributes the error-probe endpoints;
/// <see cref="CreateAuthedClient"/> returns a client that sends the token.
/// </summary>
public sealed class ScionServerFactory : WebApplicationFactory<Program>
{
    /// <summary>The token the harness configures; authenticated requests send this.</summary>
    public const string TestToken = "scion-test-token";

    private readonly string? configuredToken;

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
        // In-memory settings override app defaults. The null-token case configures nothing —
        // it exists for the fail-closed startup test.
        var settings = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
        };
        if (configuredToken is not null)
        {
            settings["Scion:AuthToken"] = configuredToken;
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));

        builder.ConfigureServices(services => services.AddSingleton<IScionEndpoints>(new ErrorProbeEndpoints()));
    }
}
