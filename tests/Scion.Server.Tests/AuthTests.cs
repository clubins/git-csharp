using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Scion.Server.Tests;

/// <summary>Behavior of the bearer-token middleware through the real pipeline.</summary>
public sealed class AuthTests : IDisposable
{
    private readonly ScionServerFactory factory;
    private readonly HttpClient anonymousClient;

    public AuthTests()
    {
        factory = new ScionServerFactory();
        anonymousClient = factory.CreateClient();
    }

    public void Dispose()
    {
        anonymousClient.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task MissingToken_IsRejectedWithAuthenticateChallenge()
    {
        var response = await anonymousClient.GetAsync("/test/validation");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var challenge));
        Assert.Equal("Bearer", challenge!.Single());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unauthorized", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WrongToken_IsRejected()
    {
        var response = await SendWithTokenAsync("wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonBearerScheme_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/validation");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");

        var response = await anonymousClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidToken_ReachesTheEndpoint()
    {
        using var client = factory.CreateAuthedClient();

        var response = await client.GetAsync("/test/validation");

        // 400, not 401: the request cleared the auth gate and hit the probe endpoint's only
        // job, which is to throw a validation failure.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void ServerWithoutConfiguredToken_RefusesToStart()
    {
        using var unconfiguredFactory = new ScionServerFactory(token: null);

        var exception = Assert.ThrowsAny<Exception>(() => unconfiguredFactory.CreateClient());

        while (exception is not OptionsValidationException && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        Assert.IsType<OptionsValidationException>(exception);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/validation");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await anonymousClient.SendAsync(request);
    }
}
