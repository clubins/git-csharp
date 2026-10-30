using System.Net;
using System.Text.Json;

namespace Scion.Server.Tests;

/// <summary>
/// Pins the spec-C7 error wire contract: the status code plus stable JSON shape for each
/// ScionApiException subtype, written through the real exception-handler pipeline.
/// </summary>
public sealed class ErrorShapeTests : IDisposable
{
    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public ErrorShapeTests()
    {
        factory = new ScionServerFactory();
        client = factory.CreateAuthedClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task ValidationFailure_IsBadRequestWithValidationCode()
    {
        var problem = await GetProblemAsync("/test/validation", HttpStatusCode.BadRequest);

        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Equal("Validation failed", problem.GetProperty("title").GetString());
        Assert.Equal("Message is required.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnknownObject_IsNotFoundWithObjectDetails()
    {
        var problem = await GetProblemAsync("/test/not-found", HttpStatusCode.NotFound);

        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("changeset", problem.GetProperty("objectType").GetString());
        Assert.Equal("sha256:abc", problem.GetProperty("objectId").GetString());
        Assert.Contains("does not exist", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RefConflict_IsConflictCarryingTheCurrentRefValue()
    {
        var problem = await GetProblemAsync("/test/ref-conflict", HttpStatusCode.Conflict);

        Assert.Equal("ref_conflict", problem.GetProperty("code").GetString());
        Assert.Equal("main", problem.GetProperty("ref").GetString());
        Assert.Equal("sha256:def", problem.GetProperty("currentTarget").GetString());
        Assert.Equal(7, problem.GetProperty("currentRevision").GetInt64());
    }

    private async Task<JsonElement> GetProblemAsync(string requestUri, HttpStatusCode expectedStatus)
    {
        var response = await client.GetAsync(requestUri);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();

        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        return root;
    }
}
