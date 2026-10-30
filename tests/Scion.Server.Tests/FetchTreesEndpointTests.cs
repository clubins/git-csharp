using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Scion.Core;
using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of POST /rpc/trees/missing through the real pipeline: the wanted-object
/// fetch walk over fixture repos (by changeset id and by tree hash), correct missing sets when
/// referenced blob content is absent, and the sparse-profile / wanted-path filters restricting
/// the walk's scope — plus the rejection contract (unknown roots 404, malformed shapes 400).
/// </summary>
public sealed class FetchTreesEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Author TestAuthor = new("Agent", "agent@scion.test");
    private const long Timestamp = 1_790_000_000;

    private static readonly (string Path, string Content)[] RootFixture =
    [
        ("docs/readme.md", "docs: the beginning"),
        ("src/Scion.Core/A.cs", "class A { }"),
        ("src/Scion.Core/B.cs", "class B { }"),
        ("lib/dep.txt", "dependency"),
    ];

    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public FetchTreesEndpointTests()
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
    public async Task WalkByChangesetId_WholeTree_EveryBlobPresentAndNothingMissing()
    {
        var root = await CommitRootAsync();

        var (rootTree, present, missing) = await WalkAsync(new { changesetId = root.Id });

        Assert.Equal(TreeOf(root), rootTree);
        Assert.Equal(Sorted(RootBlobs()), present);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task WalkByChangesetId_FollowsTheWalkedTree_RootAndChildDiffer()
    {
        var root = await CommitRootAsync();
        var (child, oldBlob, newBlob) = await CommitReadmeChangeAsync(root);
        var unchanged = RootBlobs().Where(hash => hash != oldBlob.Hash).ToList();

        var (childTree, childPresent, childMissing) = await WalkAsync(new { changesetId = child.Id });
        Assert.Equal(TreeOf(child), childTree);
        Assert.Equal(Sorted([newBlob.Hash, .. unchanged]), childPresent);
        Assert.DoesNotContain(oldBlob.Hash, childPresent);
        Assert.DoesNotContain(oldBlob.Hash, childMissing);

        // The parent's walk answers from the parent's tree: the child's new blob is not merely
        // missing there — it is outside the walk entirely, in neither list.
        var (_, rootPresent, rootMissing) = await WalkAsync(new { changesetId = root.Id });
        Assert.Equal(Sorted([oldBlob.Hash, .. unchanged]), rootPresent);
        Assert.DoesNotContain(newBlob.Hash, rootPresent);
        Assert.DoesNotContain(newBlob.Hash, rootMissing);
    }

    [Fact]
    public async Task WalkByRootTreeHash_ResolvesByContentAddress()
    {
        var root = await CommitRootAsync();
        var (child, _, _) = await CommitReadmeChangeAsync(root);
        var treeId = TreeOf(child);

        var byHash = await WalkAsync(new { rootTree = treeId });
        var byChangeset = await WalkAsync(new { changesetId = child.Id });

        Assert.Equal(byChangeset.RootTree, byHash.RootTree);
        Assert.Equal(byChangeset.Present, byHash.Present);
        Assert.Equal(byChangeset.Missing, byHash.Missing);

        // Content addresses compare case-insensitively — the uppercase spelling is the same tree.
        var (upperTree, upperPresent, _) = await WalkAsync(new { rootTree = treeId.ToUpperInvariant() });
        Assert.Equal(treeId, upperTree);
        Assert.Equal(byChangeset.Present, upperPresent);
    }

    [Fact]
    public async Task WantedPaths_RestrictTheWalkScope()
    {
        var root = await CommitRootAsync();

        var (rootTree, present, missing) = await WalkAsync(new { changesetId = root.Id, wantedPaths = new[] { "src/**" } });

        Assert.Equal(TreeOf(root), rootTree);
        Assert.Equal(Sorted(BlobsForPaths("src/Scion.Core/A.cs", "src/Scion.Core/B.cs")), present);
        foreach (var outOfScope in BlobsForPaths("docs/readme.md", "lib/dep.txt"))
        {
            Assert.DoesNotContain(outOfScope, present);
            Assert.DoesNotContain(outOfScope, missing);
        }
    }

    [Fact]
    public async Task SparseProfile_RestrictsTheWalkScope()
    {
        var root = await CommitRootAsync();

        var (_, profileOnly, _) = await WalkAsync(new { changesetId = root.Id, sparseProfile = new[] { "src/Scion.Core/**" } });
        Assert.Equal(Sorted(BlobsForPaths("src/Scion.Core/A.cs", "src/Scion.Core/B.cs")), profileOnly);

        // The profile narrows what the wanted paths select — it never widens them.
        var (_, intersection, _) = await WalkAsync(new
        {
            changesetId = root.Id,
            wantedPaths = new[] { "src/**" },
            sparseProfile = new[] { "src/Scion.Core/B.cs" },
        });
        Assert.Equal(Sorted(BlobsForPaths("src/Scion.Core/B.cs")), intersection);
    }

    [Fact]
    public async Task EmptyFilterList_IsUnrestricted()
    {
        var root = await CommitRootAsync();

        var (_, present, missing) = await WalkAsync(new { changesetId = root.Id, wantedPaths = Array.Empty<string>() });

        Assert.Equal(Sorted(RootBlobs()), present);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task LostBlobFile_IsReportedMissingWhileTheRestStayPresent()
    {
        var root = await CommitRootAsync();
        var lost = BlobFor(RootFixture[3].Content).Hash;
        var blobStore = factory.Services.GetRequiredService<BlobStore>();
        File.Delete(Directory.EnumerateFiles(blobStore.RootPath, lost, SearchOption.AllDirectories).Single());

        var (_, present, missing) = await WalkAsync(new { changesetId = root.Id });

        Assert.Equal([lost], missing);
        var remaining = RootBlobs().Where(hash => hash != lost).ToList();
        Assert.Equal(Sorted(remaining), present);
    }

    [Fact]
    public async Task UnknownChangesetId_Is404NotFound()
    {
        var nowhere = BlobFor("no such changeset").Hash;

        var (response, json) = await WalkRawAsync(new { changesetId = nowhere });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownRootTree_Is404NotFound()
    {
        var nowhere = BlobFor("no such tree").Hash;

        var (response, json) = await WalkRawAsync(new { rootTree = nowhere });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedHash_Is400Validation()
    {
        var (response, json) = await WalkRawAsync(new { changesetId = "not-a-hash" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExactlyOneTreeName_IsEnforced()
    {
        var root = await CommitRootAsync();

        var (neither, neitherBody) = await WalkRawAsync(new { });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
        Assert.Equal("validation", neitherBody.GetProperty("code").GetString());

        var (both, bothBody) = await WalkRawAsync(new { changesetId = root.Id, rootTree = TreeOf(root) });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal("validation", bothBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NullOrEmptyPatternEntry_Is400Validation()
    {
        var root = await CommitRootAsync();

        var (response, json) = await WalkRawAsync(new { changesetId = root.Id, wantedPaths = new string?[] { null } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WalkWithoutToken_Is401()
    {
        using var unauthenticated = factory.CreateClient();

        var response = await unauthenticated.PostAsync(
            "/rpc/trees/missing",
            new StringContent("""{"changesetId":"0000000000000000000000000000000000000000000000000000000000000000"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Walk_EntriesCarryTheScopedPathsWithTheirHashesAndKinds()
    {
        var root = await CommitRootAsync();

        var (response, json) = await WalkRawAsync(new { changesetId = root.Id, wantedPaths = new[] { "src/**" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = json.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "src/Scion.Core/A.cs", "src/Scion.Core/B.cs" },
            entries.Select(entry => entry.GetProperty("path").GetString()).ToArray());
        foreach (var entry in entries)
        {
            Assert.Equal(
                BlobFor(RootFixture.Single(fixture => fixture.Path == entry.GetProperty("path").GetString()).Content).Hash,
                entry.GetProperty("blobHash").GetString());
        }

        // The kind rides along — the working-copy materializer's write instruction.
        Assert.Equal("regular", entries[0].GetProperty("kind").GetString());
    }

    private async Task<Changeset> CommitRootAsync()
    {
        var changeset = Changeset.Create(
            [],
            RootFixture.Select(f => new ChangeEntry(f.Path, null, BlobFor(f.Content).Hash, FileContentKind.Regular, ChangeType.Added))
                .ToImmutableArray(),
            "fixture root",
            TestAuthor,
            Timestamp);

        var response = await PostChangesetAsync(changeset, RootFixture.Select(f => BlobFor(f.Content)).ToList());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset;
    }

    private async Task<(Changeset Changeset, Blob OldBlob, Blob NewBlob)> CommitReadmeChangeAsync(Changeset parent)
    {
        var oldBlob = BlobFor(RootFixture[0].Content);
        var newBlob = BlobFor("docs: the continuation");
        var child = Changeset.Create(
            [parent.Id],
            [new ChangeEntry(RootFixture[0].Path, oldBlob.Hash, newBlob.Hash, FileContentKind.Regular, ChangeType.Modified)],
            "edit readme",
            TestAuthor,
            Timestamp + 100);

        var response = await PostChangesetAsync(child, [newBlob]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (child, oldBlob, newBlob);
    }

    private async Task<HttpResponseMessage> PostChangesetAsync(Changeset changeset, IReadOnlyList<Blob>? blobs = null)
    {
        var payload = new
        {
            id = changeset.Id,
            parents = changeset.Parents.ToArray(),
            changes = changeset.Changes.ToArray(),
            message = changeset.Message,
            author = new { name = changeset.Author.Name, email = changeset.Author.Email },
            timestampUtc = changeset.TimestampUtc,
            blobs = (blobs ?? []).Select(b => new { hash = b.Hash, content = b.Content.ToArray() }),
        };
        using var body = new StringContent(JsonSerializer.Serialize(payload, RequestJson), Encoding.UTF8, "application/json");
        return await client.PostAsync("/rpc/changesets", body);
    }

    private string TreeOf(Changeset changeset)
    {
        var engine = factory.Services.GetRequiredService<DerivedDataEngine>();
        return engine.Get(TreeSnapshotDerivedData.TypeName, changeset.Id).Hash;
    }

    private async Task<(string RootTree, List<string> Present, List<string> Missing)> WalkAsync(object body)
    {
        var (response, json) = await WalkRawAsync(body);
        return (
            json.GetProperty("rootTree").GetString()!,
            json.GetProperty("presentBlobs").EnumerateArray().Select(e => e.GetString()!).ToList(),
            json.GetProperty("missingBlobs").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    private async Task<(HttpResponseMessage Response, JsonElement Json)> WalkRawAsync(object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, RequestJson), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/rpc/trees/missing", content);
        return (response, await ReadJsonAsync(response));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();
        return root;
    }

    private static Blob BlobFor(string content)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(content));
    }

    private static IEnumerable<string> RootBlobs()
    {
        return BlobsForPaths(RootFixture.Select(f => f.Path).ToArray());
    }

    private static IEnumerable<string> BlobsForPaths(params string[] paths)
    {
        return paths.Select(path => BlobFor(RootFixture.Single(f => f.Path == path).Content).Hash);
    }

    private static List<string> Sorted(IEnumerable<string> hashes)
    {
        return hashes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(hash => hash, StringComparer.Ordinal).ToList();
    }
}
