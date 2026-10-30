using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scion.Core;
using Scion.Server.Endpoints;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of the diff and blame query endpoints through the real pipeline: the
/// delta between any two changesets' tree snapshots (forward, reverse, rename-convention,
/// identical trees), and per-line blame from the path-history chain walked through the line
/// diff (modified and appended lines, renames threading history, re-adds, and a queried
/// changeset that never touched the path). Error contract: unknown changesets and paths are
/// 404 (spec C7), missing parameters are 400.
/// </summary>
public sealed class QueryDiffBlameEndpointTests : IDisposable
{
    private static readonly Author TestAuthor = new("Agent", "agent@scion.test");

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    /// <summary>Blob contents by hash for the changesets this test built — the request payload's source.</summary>
    private readonly Dictionary<string, byte[]> contents = new(StringComparer.Ordinal);

    public QueryDiffBlameEndpointTests()
    {
        factory = new ScionServerFactory();
        client = factory.CreateAuthedClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    // --- /rpc/diff ---

    [Fact]
    public async Task Diff_BetweenParentAndChild_ReturnsTheChangedPathsInCanonicalOrder()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000,
            Added("docs/readme.md", "docs: the beginning"),
            Added("src/app.cs", "Console.WriteLine(\"hi\");"));
        var child = await CommitAsync(root.Id, "extend", 1_700_000_100,
            Modified("docs/readme.md", root.Changes[0].NewBlob!, "docs: more"),
            Added("src/util.cs", "util"));

        var (body, response) = await GetDiffAsync(from: root.Id, to: child.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(root.Id, body.GetProperty("from").GetString());
        Assert.Equal(child.Id, body.GetProperty("to").GetString());

        var changes = body.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        // Canonical order is by path: docs/readme.md before src/util.cs.
        Assert.Equal("docs/readme.md", changes[0].GetProperty("path").GetString());
        Assert.Equal("modified", changes[0].GetProperty("type").GetString());
        Assert.Equal(root.Changes[0].NewBlob, changes[0].GetProperty("oldBlob").GetString());
        Assert.Equal(child.Changes[0].NewBlob, changes[0].GetProperty("newBlob").GetString());
        Assert.Equal("src/util.cs", changes[1].GetProperty("path").GetString());
        Assert.Equal("added", changes[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("oldBlob").ValueKind);
        Assert.Equal(child.Changes[1].NewBlob, changes[1].GetProperty("newBlob").GetString());
    }

    [Fact]
    public async Task Diff_InReverse_ReturnsTheInverseDelta()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));
        var child = await CommitAsync(root.Id, "extend", 1_700_000_100,
            Modified("docs/readme.md", root.Changes[0].NewBlob!, "v2"),
            Added("src/util.cs", "util"));

        var (body, _) = await GetDiffAsync(from: child.Id, to: root.Id);

        var changes = body.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("docs/readme.md", changes[0].GetProperty("path").GetString());
        Assert.Equal("modified", changes[0].GetProperty("type").GetString());
        Assert.Equal(child.Changes[0].NewBlob, changes[0].GetProperty("oldBlob").GetString());
        Assert.Equal(root.Changes[0].NewBlob, changes[0].GetProperty("newBlob").GetString());
        Assert.Equal("src/util.cs", changes[1].GetProperty("path").GetString());
        Assert.Equal("deleted", changes[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("newBlob").ValueKind);
    }

    [Fact]
    public async Task Diff_OfARename_FollowsTheDeletedPlusRenamedConvention()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/a.md", "shared content\n"));
        var renamed = await CommitAsync(root.Id, "rename", 1_700_000_100,
            Deleted("docs/a.md", root.Changes[0].NewBlob!),
            Renamed("docs/b.md", root.Changes[0].NewBlob!));

        var (body, _) = await GetDiffAsync(from: root.Id, to: renamed.Id);

        var changes = body.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("docs/a.md", changes[0].GetProperty("path").GetString());
        Assert.Equal("deleted", changes[0].GetProperty("type").GetString());
        Assert.Equal("docs/b.md", changes[1].GetProperty("path").GetString());
        Assert.Equal("renamed", changes[1].GetProperty("type").GetString());
        Assert.Equal(root.Changes[0].NewBlob, changes[1].GetProperty("oldBlob").GetString());
        Assert.Equal(root.Changes[0].NewBlob, changes[1].GetProperty("newBlob").GetString());
    }

    [Fact]
    public async Task Diff_OfIdenticalTrees_IsEmpty()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));
        var branch = await CommitAsync(root.Id, "unrelated", 1_700_000_100, Added("other.txt", "unrelated"));
        var merged = await CommitMergeAsync(root.Id, branch.Id, 1_700_000_200);

        var (body, response) = await GetDiffAsync(from: root.Id, to: merged.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task Diff_WithAnUnknownEndpoint_Is404()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));

        var (fromBody, fromResponse) = await GetDiffAsync(from: UnknownId(), to: root.Id);
        Assert.Equal(HttpStatusCode.NotFound, fromResponse.StatusCode);
        Assert.Equal("not_found", CodeOf(fromBody));
        Assert.Equal("changeset", ObjectTypeOf(fromBody));

        var (toBody, toResponse) = await GetDiffAsync(from: root.Id, to: UnknownId());
        Assert.Equal(HttpStatusCode.NotFound, toResponse.StatusCode);
        Assert.Equal("not_found", CodeOf(toBody));
    }

    [Fact]
    public async Task Diff_WithoutRequiredParams_Is400()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));

        var (_, noFrom) = await GetDiffAsync(from: null, to: root.Id);
        Assert.Equal(HttpStatusCode.BadRequest, noFrom.StatusCode);

        var (_, noTo) = await GetDiffAsync(from: root.Id, to: null);
        Assert.Equal(HttpStatusCode.BadRequest, noTo.StatusCode);
    }

    // --- /rpc/blame ---

    [Fact]
    public async Task Blame_AttributesEachLineToTheChangesetThatIntroducedIt()
    {
        var first = await CommitAsync(null, "add app", 1_700_000_000, Added("src/app.cs", "alpha\nbeta\ngamma\n"));
        var tweak = await CommitAsync(first.Id, "tweak", 1_700_000_100,
            Modified("src/app.cs", first.Changes[0].NewBlob!, "alpha\nBETA\ngamma\n"));
        var extend = await CommitAsync(tweak.Id, "extend", 1_700_000_200,
            Modified("src/app.cs", tweak.Changes[0].NewBlob!, "alpha\nBETA\ngamma\ndelta\n"));

        var (body, response) = await GetBlameAsync(extend.Id, "src/app.cs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(extend.Id, body.GetProperty("changeset").GetString());
        Assert.Equal("src/app.cs", body.GetProperty("path").GetString());

        var lines = body.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(4, lines.Length);
        Assert.Equal((1, first.Id, "alpha\n"), LineOf(lines[0]));
        Assert.Equal((2, tweak.Id, "BETA\n"), LineOf(lines[1]));
        Assert.Equal((3, first.Id, "gamma\n"), LineOf(lines[2]));
        Assert.Equal((4, extend.Id, "delta\n"), LineOf(lines[3]));
    }

    [Fact]
    public async Task Blame_ThroughARename_ThreadsTheFileHistoryOntoTheNewPath()
    {
        var first = await CommitAsync(null, "add a", 1_700_000_000, Added("docs/a.md", "one\ntwo\n"));
        var renamed = await CommitAsync(first.Id, "rename a to b", 1_700_000_100,
            Deleted("docs/a.md", first.Changes[0].NewBlob!),
            Renamed("docs/b.md", first.Changes[0].NewBlob!));

        var (body, response) = await GetBlameAsync(renamed.Id, "docs/b.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The rename rewrote no lines: both blame to the adding changeset, on the new path.
        var lines = body.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal((1, first.Id, "one\n"), LineOf(lines[0]));
        Assert.Equal((2, first.Id, "two\n"), LineOf(lines[1]));
    }

    [Fact]
    public async Task Blame_AtAChangesetThatNeverTouchedThePath_AnswersWithEarlierAttributions()
    {
        var first = await CommitAsync(null, "add app", 1_700_000_000, Added("src/app.cs", "alpha\nbeta\n"));
        var other = await CommitAsync(first.Id, "add guide", 1_700_000_100, Added("docs/guide.md", "guide\n"));

        var (body, response) = await GetBlameAsync(other.Id, "src/app.cs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = body.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal((1, first.Id, "alpha\n"), LineOf(lines[0]));
        Assert.Equal((2, first.Id, "beta\n"), LineOf(lines[1]));
    }

    [Fact]
    public async Task Blame_OfADeletedThenReaddedPath_AttributesEveryLineToTheReAdd()
    {
        var first = await CommitAsync(null, "add notes", 1_700_000_000, Added("docs/notes.md", "x\ny\n"));
        var deleted = await CommitAsync(first.Id, "drop notes", 1_700_000_100, Deleted("docs/notes.md", first.Changes[0].NewBlob!));
        var readded = await CommitAsync(deleted.Id, "restore notes", 1_700_000_200, Added("docs/notes.md", "x\ny\n"));

        var (body, response) = await GetBlameAsync(readded.Id, "docs/notes.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = body.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(readded.Id, line.GetProperty("changesetId").GetString()));
    }

    [Fact]
    public async Task Blame_WithAnUnknownChangeset_Is404()
    {
        await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));

        var (unknownBody, unknownResponse) = await GetBlameAsync(UnknownId(), "docs/readme.md");

        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
        Assert.Equal("not_found", CodeOf(unknownBody));
        Assert.Equal("changeset", ObjectTypeOf(unknownBody));
    }

    [Fact]
    public async Task Blame_ForAPathAbsentFromTheTree_Is404()
    {
        var root = await CommitAsync(null, "add readme", 1_700_000_000, Added("docs/readme.md", "v1"));

        // Never touched anywhere in the lineage.
        var (neverBody, neverResponse) = await GetBlameAsync(root.Id, "docs/never.md");
        Assert.Equal(HttpStatusCode.NotFound, neverResponse.StatusCode);
        Assert.Equal("not_found", CodeOf(neverBody));
        Assert.Equal("path", ObjectTypeOf(neverBody));

        // Deleted before the queried changeset: no file, no lines to blame.
        var deleted = await CommitAsync(root.Id, "drop readme", 1_700_000_100, Deleted("docs/readme.md", root.Changes[0].NewBlob!));
        var (deletedBody, deletedResponse) = await GetBlameAsync(deleted.Id, "docs/readme.md");
        Assert.Equal(HttpStatusCode.NotFound, deletedResponse.StatusCode);
        Assert.Equal("path", ObjectTypeOf(deletedBody));
    }

    [Fact]
    public async Task Blame_WithoutRequiredParams_Is400()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));

        var (_, noChangeset) = await GetBlameAsync(changeset: null, "docs/readme.md");
        Assert.Equal(HttpStatusCode.BadRequest, noChangeset.StatusCode);

        var (_, noPath) = await GetBlameAsync(root.Id, path: null);
        Assert.Equal(HttpStatusCode.BadRequest, noPath.StatusCode);
    }

    // --- fixture plumbing ---

    private async Task<Changeset> CommitAsync(string? parent, string message, long timestamp, params ChangeEntry[] changes)
    {
        var changeset = Changeset.Create(
            parent is null ? [] : [parent],
            [.. changes],
            message,
            TestAuthor,
            timestamp);

        var request = new CreateChangesetRequest
        {
            Id = changeset.Id,
            Parents = [.. changeset.Parents],
            Changes = [.. changeset.Changes],
            Message = changeset.Message,
            Author = changeset.Author,
            TimestampUtc = changeset.TimestampUtc,
            Blobs = changeset.Changes
                .Where(change => change.NewBlob is not null)
                .Select(change => new CreateChangesetRequest.BlobPayload(change.NewBlob!, contents[change.NewBlob!]))
                .ToList(),
        };

        var response = await client.PostAsJsonAsync("/rpc/changesets", request, RequestJson);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset;
    }

    private async Task<Changeset> CommitMergeAsync(string firstParent, string secondParent, long timestamp)
    {
        var changeset = Changeset.Create([firstParent, secondParent], [], "merge side two into side one", TestAuthor, timestamp);
        var request = new CreateChangesetRequest
        {
            Id = changeset.Id,
            Parents = [.. changeset.Parents],
            Changes = [],
            Message = changeset.Message,
            Author = changeset.Author,
            TimestampUtc = changeset.TimestampUtc,
        };

        var response = await client.PostAsJsonAsync("/rpc/changesets", request, RequestJson);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset;
    }

    private ChangeEntry Added(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Blob.Create(bytes).Hash;
        contents[hash] = bytes;
        return new ChangeEntry(path, null, hash, FileContentKind.Regular, ChangeType.Added);
    }

    private ChangeEntry Modified(string path, string previousHash, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Blob.Create(bytes).Hash;
        contents[hash] = bytes;
        return new ChangeEntry(path, previousHash, hash, FileContentKind.Regular, ChangeType.Modified);
    }

    private ChangeEntry Deleted(string path, string previousHash)
    {
        return new ChangeEntry(path, previousHash, null, FileContentKind.Regular, ChangeType.Deleted);
    }

    /// <summary>The tree-diff rename convention's new-path entry: the same blob on both sides — a rename rewrites nothing.</summary>
    private ChangeEntry Renamed(string newPath, string blobHash)
    {
        return new ChangeEntry(newPath, blobHash, blobHash, FileContentKind.Regular, ChangeType.Renamed);
    }

    private async Task<(JsonElement Body, HttpResponseMessage Response)> GetDiffAsync(string? from, string? to)
    {
        List<string> query = [];
        if (from is not null)
        {
            query.Add($"from={from}");
        }

        if (to is not null)
        {
            query.Add($"to={to}");
        }

        var separator = query.Count == 0 ? string.Empty : "?";
        var response = await client.GetAsync($"/rpc/diff{separator}{string.Join('&', query)}");
        return (await JsonBodyAsync(response), response);
    }

    private async Task<(JsonElement Body, HttpResponseMessage Response)> GetBlameAsync(string? changeset, string? path)
    {
        List<string> query = [];
        if (changeset is not null)
        {
            query.Add($"changeset={changeset}");
        }

        if (path is not null)
        {
            query.Add($"path={Uri.EscapeDataString(path)}");
        }

        var separator = query.Count == 0 ? string.Empty : "?";
        var response = await client.GetAsync($"/rpc/blame{separator}{string.Join('&', query)}");
        return (await JsonBodyAsync(response), response);
    }

    private static async Task<JsonElement> JsonBodyAsync(HttpResponseMessage response)
    {
        // Read as a string, then parse: exception-handler responses arrive with the raw stream
        // already closed under the test server, so ReadAsStreamAsync throws on them (the same
        // reason ErrorShapeTests reads error bodies as text).
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static (int Line, string ChangesetId, string Content) LineOf(JsonElement line)
    {
        return (
            line.GetProperty("line").GetInt32(),
            line.GetProperty("changesetId").GetString()!,
            line.GetProperty("content").GetString()!);
    }

    private static string CodeOf(JsonElement problem)
    {
        return problem.GetProperty("code").GetString()!;
    }

    private static string ObjectTypeOf(JsonElement problem)
    {
        return problem.GetProperty("objectType").GetString()!;
    }

    private static string UnknownId()
    {
        return new string('a', 64);
    }
}
