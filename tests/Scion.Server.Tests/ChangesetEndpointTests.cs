using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Scion.Core;
using Scion.Core.Storage;
using Scion.Server.Endpoints;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of POST /rpc/changesets through the real pipeline: the commit lifecycle
/// (blobs then record then optional ref move), the CAS conflict contract (409 carrying the
/// ref's current value), and the rejections the server owes every client — re-derived ids,
/// referenced-blob existence, structural limits.
/// </summary>
public sealed class ChangesetEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Author TestAuthor = new("Agent", "agent@scion.test");

    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public ChangesetEndpointTests()
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
    public async Task CommitLifecycle_UploadBlobThenRootThenChild_AdvancesRefByCas()
    {
        var blobs = factory.Services.GetRequiredService<BlobStore>();

        // Step 1: upload a blob, then reference it in the root commit without re-sending it.
        var firstContent = Encoding.UTF8.GetBytes("docs: the beginning");
        var firstHash = Sha256.HashHex(firstContent);
        await UploadAsync(firstHash, firstContent);

        var root = Changeset.Create(
            [],
            [new ChangeEntry("docs/readme.md", null, firstHash, FileContentKind.Regular, ChangeType.Added)],
            "initial commit",
            TestAuthor,
            1_700_000_000);

        var rootResponse = await PostChangesetAsync(root, refMove: RefMove("main", RefStore.CreationRevision));
        Assert.Equal(HttpStatusCode.Created, rootResponse.StatusCode);

        var rootBody = await ReadJsonAsync(rootResponse);
        Assert.Equal(root.Id, rootBody.GetProperty("id").GetString());
        Assert.Equal("main", rootBody.GetProperty("ref").GetProperty("name").GetString());
        Assert.Equal(root.Id, rootBody.GetProperty("ref").GetProperty("target").GetString());
        Assert.Equal(1, rootBody.GetProperty("ref").GetProperty("revision").GetInt64());

        Assert.True(blobs.Exists(firstHash));
        var storedFirst = await blobs.ReadAsync(firstHash);
        Assert.True(storedFirst.Content.Span.SequenceEqual(firstContent));

        // Step 2: the child commit ships its changed blob inline and CAS-advances main.
        var secondContent = Encoding.UTF8.GetBytes("docs: the continuation");
        var child = Changeset.Create(
            [root.Id],
            [new ChangeEntry("docs/readme.md", firstHash, Blob.Create(secondContent).Hash, FileContentKind.Regular, ChangeType.Modified)],
            "extend readme",
            TestAuthor,
            1_700_000_100);

        var childResponse = await PostChangesetAsync(
            child,
            blobs: [InlineBlob(secondContent)],
            refMove: RefMove("main", 1));

        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var childBody = await ReadJsonAsync(childResponse);
        Assert.Equal(child.Id, childBody.GetProperty("id").GetString());
        Assert.Equal(2, childBody.GetProperty("ref").GetProperty("revision").GetInt64());
        Assert.Equal(child.Id, childBody.GetProperty("ref").GetProperty("target").GetString());

        var log = factory.Services.GetRequiredService<ChangesetLog>();
        Assert.Equal(2, log.Count);
        Assert.True(log.TryGetById(child.Id, out var storedChild));
        Assert.Equal(child, storedChild);
        Assert.Equal(child.Id, factory.Services.GetRequiredService<RefStore>().Get("main")!.Target);
    }

    [Fact]
    public async Task StaleRefMove_Returns409WithTheCurrentRefValue()
    {
        var root = await CommitRootAsync();
        var child = await CommitChildAsync(root);

        // A racing client that observed revision 1 and lost the move to the child commit.
        var stale = Changeset.Create(
            [child.Id],
            [new ChangeEntry("docs/other.md", null, Blob.Create(Encoding.UTF8.GetBytes("racing")).Hash, FileContentKind.Regular, ChangeType.Added)],
            "racing commit",
            TestAuthor,
            1_700_000_200);

        var response = await PostChangesetAsync(
            stale,
            blobs: [InlineBlob(Encoding.UTF8.GetBytes("racing"))],
            refMove: RefMove("main", 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("ref_conflict", problem.GetProperty("code").GetString());
        Assert.Equal("main", problem.GetProperty("ref").GetString());
        Assert.Equal(child.Id, problem.GetProperty("currentTarget").GetString());
        Assert.Equal(2, problem.GetProperty("currentRevision").GetInt64());

        // The ref did not move; the changeset itself was stored, unreachable until rebased —
        // append and ref move are deliberately decoupled (spec: commits cannot contend).
        var refs = factory.Services.GetRequiredService<RefStore>();
        Assert.Equal(child.Id, refs.Get("main")!.Target);
        Assert.Equal(2, refs.Get("main")!.Revision);
        Assert.True(factory.Services.GetRequiredService<ChangesetLog>().Contains(stale.Id));
    }

    [Fact]
    public async Task ClientIdMismatch_IsRejectedBeforeAnythingIsStored()
    {
        var content = Encoding.UTF8.GetBytes("a commit whose claimed id is a lie");
        var changeset = Changeset.Create(
            [],
            [new ChangeEntry("file.txt", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            "lying about the id",
            TestAuthor,
            1_700_000_000);
        var wrongId = Sha256.HashHex(Encoding.UTF8.GetBytes("some other payload"));

        var response = await PostChangesetAsync(changeset, blobs: [InlineBlob(content)], idOverride: wrongId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Contains(wrongId, problem.GetProperty("detail").GetString());
        Assert.Contains(changeset.Id, problem.GetProperty("detail").GetString());

        // Validation precedes every write: neither the blob nor the changeset landed.
        var store = factory.Services.GetRequiredService<BlobStore>();
        Assert.False(store.Exists(Blob.Create(content).Hash));
        Assert.Equal(0, factory.Services.GetRequiredService<ChangesetLog>().Count);
    }

    [Fact]
    public async Task ReferencedBlobMissing_IsRejectedWithoutStoring()
    {
        var neverUploaded = Sha256.HashHex(Encoding.UTF8.GetBytes("content no one uploaded"));
        var changeset = Changeset.Create(
            [],
            [new ChangeEntry("file.txt", null, neverUploaded, FileContentKind.Regular, ChangeType.Added)],
            "references a ghost",
            TestAuthor,
            1_700_000_000);

        var response = await PostChangesetAsync(changeset);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Contains(neverUploaded, problem.GetProperty("detail").GetString());
        Assert.Equal(0, factory.Services.GetRequiredService<ChangesetLog>().Count);
    }

    [Fact]
    public async Task DuplicateInlineBlobs_AreAccepted()
    {
        var content = Encoding.UTF8.GetBytes("sent twice in one request, stored once");
        var changeset = Changeset.Create(
            [],
            [new ChangeEntry("file.txt", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            "duplicate blob payload",
            TestAuthor,
            1_700_000_000);

        var response = await PostChangesetAsync(changeset, blobs: [InlineBlob(content), InlineBlob(content)]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var store = factory.Services.GetRequiredService<BlobStore>();
        var stored = await store.ReadAsync(Blob.Create(content).Hash);
        Assert.True(stored.Content.Span.SequenceEqual(content));
    }

    [Fact]
    public async Task RepostingTheSameChangeset_IsIdempotent()
    {
        var content = new byte[] { 1, 2, 3 };
        var changeset = Changeset.Create(
            [],
            [new ChangeEntry("file.txt", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            "posted twice",
            TestAuthor,
            1_700_000_000);

        var first = await PostChangesetAsync(changeset, blobs: [InlineBlob(content)]);
        var second = await PostChangesetAsync(changeset, blobs: [InlineBlob(content)]);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<ChangesetLog>().Count);
    }

    [Fact]
    public async Task TooManyParents_IsRejected()
    {
        // Built as raw JSON: Changeset.Create refuses three parents, so the invalid record can
        // only exist on the wire — where the server's own limit check must reject it.
        var payload = new
        {
            id = new string('a', 64),
            parents = new[]
            {
                Sha256.HashHex(Encoding.UTF8.GetBytes("parent 0")),
                Sha256.HashHex(Encoding.UTF8.GetBytes("parent 1")),
                Sha256.HashHex(Encoding.UTF8.GetBytes("parent 2")),
            },
            changes = Array.Empty<object>(),
            message = "impossible merge",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_000,
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload, RequestJson), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/rpc/changesets", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedParentId_IsRejected()
    {
        var changeset = Changeset.Create(
            ["deadbeef"],
            [],
            "bad parent",
            TestAuthor,
            1_700_000_000);

        var response = await PostChangesetAsync(changeset);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateChangeset_RequiresBearerToken()
    {
        using var anonymous = factory.CreateClient();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await anonymous.PostAsync("/rpc/changesets", body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Commits the root changeset and creates <c>main</c> at revision 1.</summary>
    private async Task<Changeset> CommitRootAsync()
    {
        var content = Encoding.UTF8.GetBytes("the root of history");
        var root = Changeset.Create(
            [],
            [new ChangeEntry("docs/readme.md", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            "initial commit",
            TestAuthor,
            1_700_000_000);

        var response = await PostChangesetAsync(
            root,
            blobs: [InlineBlob(content)],
            refMove: RefMove("main", RefStore.CreationRevision));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return root;
    }

    /// <summary>Commits a child of <paramref name="root"/> and advances <c>main</c> to revision 2.</summary>
    private async Task<Changeset> CommitChildAsync(Changeset root)
    {
        var content = Encoding.UTF8.GetBytes("the continuation");
        var child = Changeset.Create(
            [root.Id],
            [new ChangeEntry("docs/readme.md", Blob.Create(Encoding.UTF8.GetBytes("the root of history")).Hash, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Modified)],
            "extend readme",
            TestAuthor,
            1_700_000_100);

        var response = await PostChangesetAsync(
            child,
            blobs: [InlineBlob(content)],
            refMove: RefMove("main", 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return child;
    }

    private async Task<HttpResponseMessage> PostChangesetAsync(
        Changeset changeset,
        List<CreateChangesetRequest.BlobPayload>? blobs = null,
        CreateChangesetRequest.RefMoveRequest? refMove = null,
        string? idOverride = null)
    {
        var payload = new
        {
            id = idOverride ?? changeset.Id,
            parents = changeset.Parents.ToArray(),
            changes = changeset.Changes.ToArray(),
            message = changeset.Message,
            author = new { name = changeset.Author.Name, email = changeset.Author.Email },
            timestampUtc = changeset.TimestampUtc,
            blobs,
            refMove,
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload, RequestJson), Encoding.UTF8, "application/json");
        return await client.PostAsync("/rpc/changesets", body);
    }

    private static CreateChangesetRequest.BlobPayload InlineBlob(byte[] content)
    {
        return new CreateChangesetRequest.BlobPayload(Sha256.HashHex(content), content);
    }

    private static CreateChangesetRequest.RefMoveRequest RefMove(string name, long expectedRevision)
    {
        return new CreateChangesetRequest.RefMoveRequest(name, expectedRevision);
    }

    /// <summary>Uploads raw content to the blob endpoint, asserting the store accepted it.</summary>
    private async Task UploadAsync(string hash, byte[] content)
    {
        using var body = new ByteArrayContent(content);
        var response = await client.PostAsync($"/rpc/blobs/{hash}", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();
        return root;
    }
}
