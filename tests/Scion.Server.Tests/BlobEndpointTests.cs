using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Scion.Core;
using Scion.Core.Storage;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of POST /rpc/blobs/{hash} through the real pipeline: content lands under
/// its address, re-uploads deduplicate, and the address is enforced — a body that does not hash
/// to the route's hash is rejected without storing anything.
/// </summary>
public sealed class BlobEndpointTests : IDisposable
{
    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public BlobEndpointTests()
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
    public async Task Upload_StoresContentUnderItsHash()
    {
        var content = Encoding.UTF8.GetBytes("the source of a changed file");

        var response = await UploadAsync(content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(Sha256.HashHex(content), body.GetProperty("hash").GetString());

        var blobs = factory.Services.GetRequiredService<BlobStore>();
        var stored = await blobs.ReadAsync(Sha256.HashHex(content));
        Assert.True(stored.Content.Span.SequenceEqual(content));
    }

    [Fact]
    public async Task DuplicateUpload_IsANoopAndSucceeds()
    {
        var content = Encoding.UTF8.GetBytes("uploaded twice, stored once");
        var hash = Sha256.HashHex(content);

        var first = await UploadAsync(content);
        var second = await UploadAsync(content);

        // First store creates (201); the re-upload is the idempotent no-op (200).
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(hash, (await ReadJsonAsync(second)).GetProperty("hash").GetString());

        // Both uploads converge on one stored blob: readable, and still exactly its address.
        var blobs = factory.Services.GetRequiredService<BlobStore>();
        var stored = await blobs.ReadAsync(hash);
        Assert.True(stored.Content.Span.SequenceEqual(content));
        Assert.Equal(hash, stored.Hash);
    }

    [Fact]
    public async Task UppercaseRouteHash_IsNormalizedToLowercase()
    {
        var content = Encoding.UTF8.GetBytes("addressed case-insensitively");
        var hash = Sha256.HashHex(content);

        var response = await UploadAsync(content, hash.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(hash, body.GetProperty("hash").GetString());
        Assert.True(factory.Services.GetRequiredService<BlobStore>().Exists(hash));
    }

    [Fact]
    public async Task HashMismatch_IsRejectedWithoutStoring()
    {
        var content = Encoding.UTF8.GetBytes("content that will be addressed by the wrong hash");
        var wrongHash = Sha256.HashHex(Encoding.UTF8.GetBytes("something else"));

        var response = await UploadAsync(content, wrongHash);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.False(factory.Services.GetRequiredService<BlobStore>().Exists(Sha256.HashHex(content)));
    }

    [Fact]
    public async Task MalformedRouteHash_IsRejected()
    {
        using var body = new ByteArrayContent([1, 2, 3]);

        var response = await client.PostAsync("/rpc/blobs/not-a-hash", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmptyBlob_IsAccepted()
    {
        var response = await UploadAsync([]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(Sha256.HashHex([]), body.GetProperty("hash").GetString());
    }

    [Fact]
    public async Task Upload_RequiresBearerToken()
    {
        using var anonymous = factory.CreateClient();
        using var body = new ByteArrayContent([1, 2, 3]);

        var response = await anonymous.PostAsync($"/rpc/blobs/{Sha256.HashHex([1, 2, 3])}", body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> UploadAsync(byte[] content, string? hashOverride = null)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync($"/rpc/blobs/{hashOverride ?? Sha256.HashHex(content)}", body);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();
        return root;
    }
}
