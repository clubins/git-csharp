using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scion.Server;
using Scion.Server.Auth;

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

            services.AddSingleton<IScionEndpoints>(new ErrorProbeEndpoints());
        });
    }
}
