using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Scion.Core;
using Scion.Core.Storage;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of POST /rpc/blobs/missing through the real pipeline: the fetch protocol's
/// read half. A request naming stored addresses is answered with a hash-keyed, length-chunked
/// stream of those blobs; a request naming an address the store lacks is a 404 before any stream
/// starts (spec C7); a large blob round-trips through many chunks without any whole-blob buffering.
/// </summary>
public sealed class FetchBlobsEndpointTests : IDisposable
{
    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public FetchBlobsEndpointTests()
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
    public async Task MissingBlobs_StreamsRequestedContentBackHashKeyed()
    {
        var contents = new[]
        {
            Encoding.UTF8.GetBytes("first file, changed by an agent"),
            [],
            Enumerable.Range(0, 4352).Select(i => (byte)(i % 256)).ToArray(),
        };
        foreach (var content in contents)
        {
            await UploadAsync(content);
        }

        // Request in a different order than the blobs were stored: the stream must follow the request.
        var requested = new[]
        {
            Sha256.HashHex(contents[2]),
            Sha256.HashHex(contents[0]),
            Sha256.HashHex(contents[1]),
        };
        var expectedInRequestOrder = new[] { contents[2], contents[0], contents[1] };

        using var response = await client.PostAsync("/rpc/blobs/missing", JsonBody(requested));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync();
        var entries = await ReadEntriesAsync(body, requested.Length);
        Assert.True(body.ReadByte() < 0, "the stream carried bytes beyond the last entry");

        Assert.Equal(
            requested,
            entries.Select(entry => entry.Hash).ToArray());
        Assert.Equal(
            expectedInRequestOrder,
            entries.Select(entry => entry.Content).ToArray());
        Assert.All(entries, entry => Assert.True(entry.ChunkCount <= 1, "small content fragmented into multiple chunks"));
    }

    [Fact]
    public async Task UnknownHash_IsRejected404BeforeAnythingStreams()
    {
        var known = Encoding.UTF8.GetBytes("stored, but the request names a stranger too");
        await UploadAsync(known);
        var unknown = new string('a', 64);

        using var response = await client.PostAsync(
            "/rpc/blobs/missing", JsonBody(Sha256.HashHex(known), unknown));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("blob", problem.GetProperty("objectType").GetString());
        Assert.Equal(unknown, problem.GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task MalformedHash_IsRejected()
    {
        using var response = await client.PostAsync("/rpc/blobs/missing", JsonBody("not-a-hash"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LargeBlob_StreamsThroughManyLengthPrefixedChunks()
    {
        // Far larger than the server's copy buffer, so the entry is necessarily multi-chunk —
        // the wire proof that no whole-blob buffering stands between the store and the response.
        var content = new byte[(3 * 1024 * 1024) + 137];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        var blobs = factory.Services.GetRequiredService<BlobStore>();
        await blobs.WriteAsync(new Blob(Sha256.HashHex(content), content));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/rpc/blobs/missing")
        {
            Content = JsonBody(Sha256.HashHex(content)),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        var entries = await ReadEntriesAsync(body, expectedEntries: 1);

        var entry = Assert.Single(entries);
        Assert.Equal(Sha256.HashHex(content), entry.Hash);
        Assert.True(entry.Content.SequenceEqual(content));
        // Pinned exactly: the server's 80 KiB copy buffer yields 39 chunks for this size. The pin
        // is the wire proof that a multi-megabyte blob crosses many bounded chunks — and if the
        // buffer size ever changes, this fails loudly and the number is re-derived, not deleted.
        Assert.Equal(39, entry.ChunkCount);
    }

    [Fact]
    public async Task UppercaseHashes_AreNormalizedToLowercase()
    {
        var content = Encoding.UTF8.GetBytes("addressed case-insensitively, fetched the same way");
        await UploadAsync(content);

        using var response = await client.PostAsync(
            "/rpc/blobs/missing", JsonBody(Sha256.HashHex(content).ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        var entries = await ReadEntriesAsync(body, expectedEntries: 1);

        var entry = Assert.Single(entries);
        Assert.Equal(Sha256.HashHex(content), entry.Hash);
        Assert.True(entry.Content.SequenceEqual(content));
    }

    [Fact]
    public async Task DuplicateHashes_AreServedOnce()
    {
        var content = Encoding.UTF8.GetBytes("asked for twice, streamed once");
        await UploadAsync(content);
        var hash = Sha256.HashHex(content);

        using var response = await client.PostAsync("/rpc/blobs/missing", JsonBody(hash, hash));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        var entries = await ReadEntriesAsync(body, expectedEntries: 1);

        var entry = Assert.Single(entries);
        Assert.Equal(hash, entry.Hash);
    }

    [Fact]
    public async Task EmptyBlobList_YieldsAnEmptyStream()
    {
        using var response = await client.PostAsync("/rpc/blobs/missing", JsonBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MissingBlobs_RequiresBearerToken()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync("/rpc/blobs/missing", JsonBody(new string('a', 64)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FetchRoute_IsNotCapturedByTheUploadRoute()
    {
        // "missing" is a valid {hash} route shape only in spirit — the literal segment must win
        // routing, or the fetch half of the protocol answers as if the body were a blob upload.
        var content = Encoding.UTF8.GetBytes("content whose fetch must not be swallowed by the upload route");
        await UploadAsync(content);

        using var response = await client.PostAsync(
            "/rpc/blobs/missing", JsonBody(Sha256.HashHex(content)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> UploadAsync(byte[] content)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await client.PostAsync($"/rpc/blobs/{Sha256.HashHex(content)}", body);
        Assert.True(response.IsSuccessStatusCode, $"blob setup failed: {response.StatusCode}");
        return response;
    }

    private static StringContent JsonBody(params string[] hashes)
    {
        var json = JsonSerializer.Serialize(new { blobs = hashes });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();
        return root;
    }

    private sealed record WireEntry(string Hash, byte[] Content, int ChunkCount);

    /// <summary>
    /// The client-side twin of the endpoint's framing (see FetchBlobsEndpoints): reads exactly
    /// <paramref name="expectedEntries"/> hash-keyed, length-chunked entries and asserts the
    /// framing holds — hashes are 64 ASCII hex bytes, every chunk length is honored, and each
    /// entry closes with the zero-length terminator.
    /// </summary>
    private static async Task<IReadOnlyList<WireEntry>> ReadEntriesAsync(Stream stream, int expectedEntries)
    {
        List<WireEntry> entries = [];
        for (var i = 0; i < expectedEntries; i++)
        {
            var key = new byte[64];
            await ReadExactlyAsync(stream, key);
            var hash = Encoding.ASCII.GetString(key);

            using var content = new MemoryStream();
            var prefix = new byte[sizeof(ulong)];
            var buffer = new byte[64 * 1024];
            var chunks = 0;
            while (true)
            {
                await ReadExactlyAsync(stream, prefix);
                var remaining = (long)BinaryPrimitives.ReadUInt64LittleEndian(prefix);
                if (remaining == 0)
                {
                    break;
                }

                chunks++;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0)
                    {
                        throw new InvalidDataException($"Entry '{hash}' ended mid-chunk: {remaining} bytes short.");
                    }

                    await content.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
            }

            entries.Add(new WireEntry(hash, content.ToArray(), chunks));
        }

        return entries;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new InvalidDataException("Fetch stream ended mid-entry.");
            }

            offset += read;
        }
    }
}
