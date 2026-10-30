using System.Net;
using System.Text.Json;

namespace Scion.Server.Tests;

public sealed class HealthEndpointTests : IDisposable
{
    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public HealthEndpointTests()
    {
        factory = new ScionServerFactory();
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task HealthEndpoint_IsAvailableWithoutAToken()
    {
        var response = await client.GetAsync("/healthz");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }
}
