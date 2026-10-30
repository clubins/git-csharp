using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Scion.Core;
using Scion.Core.Derived;
using Scion.Core.Storage;
using Scion.Server.Endpoints;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of the history-operation endpoints (spec D3): merge-base answered over
/// DAG families, server-side merge and rebase producing changesets, and the spec-C7 contract —
/// conflict responses carry markers and kinds, unknown changesets 404, lost ref moves 409 with
/// the ref's current value while the appended changesets stay stored.
/// </summary>
public sealed class QueryMergeEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Author TestAuthor = new("Agent", "agent@scion.test");

    private readonly ScionServerFactory factory;
    private readonly HttpClient client;

    public QueryMergeEndpointTests()
    {
        factory = new ScionServerFactory();
        client = factory.CreateAuthedClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    // ---- Merge-base ------------------------------------------------------------------------

    [Fact]
    public async Task MergeBase_OnBranchFamily_AnswersTheSharedRoot()
    {
        var rootFiles = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var ours = await CommitAsync("ours edits a", [root.Id], rootTree, Files(("a.txt", "a2\n"), ("b.txt", "b1\n")), 1_700_000_100);
        var theirs = await CommitAsync("theirs edits b", [root.Id], rootTree, Files(("a.txt", "a1\n"), ("b.txt", "b2\n")), 1_700_000_200);

        var response = await client.GetAsync($"/rpc/merge-base?first={ours.Id}&second={theirs.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(ours.Id, body.GetProperty("first").GetString());
        Assert.Equal(theirs.Id, body.GetProperty("second").GetString());
        Assert.Equal(root.Id, body.GetProperty("mergeBase").GetString());
    }

    [Fact]
    public async Task MergeBase_WhenOneTipIsTheOthersAncestor_AnswersTheAncestor()
    {
        var rootFiles = Files(("a.txt", "a1\n"));
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var child = await CommitAsync("child", [root.Id], TreeOf(rootFiles), Files(("a.txt", "a2\n")), 1_700_000_100);

        var response = await client.GetAsync($"/rpc/merge-base?first={child.Id}&second={root.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(root.Id, (await ReadJsonAsync(response)).GetProperty("mergeBase").GetString());
    }

    [Fact]
    public async Task MergeBase_OnDisjointHistories_AnswersNull()
    {
        var first = await CommitAsync("first root", [], TreeSnapshot.Empty, Files(("a.txt", "a\n")), 1_700_000_000);
        var second = await CommitAsync("second root", [], TreeSnapshot.Empty, Files(("z.txt", "z\n")), 1_700_000_100);

        var response = await client.GetAsync($"/rpc/merge-base?first={first.Id}&second={second.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("mergeBase").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task MergeBase_OnCrissCross_AnswersTheDeterministicBestCandidate()
    {
        // The criss-cross family: A2 and B2 are merges of A1 and B1 in opposite orders, so both
        // are maximal common ancestors of each other and the policy must name one deterministically.
        var rootFiles = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var a1 = await CommitAsync("A1 edits a", [root.Id], rootTree, Files(("a.txt", "a2\n"), ("b.txt", "b1\n")), 1_700_000_100);
        var b1 = await CommitAsync("B1 edits b", [root.Id], rootTree, Files(("a.txt", "a1\n"), ("b.txt", "b2\n")), 1_700_000_200);
        var a1Tree = TreeOf(Files(("a.txt", "a2\n"), ("b.txt", "b1\n")));
        var b1Tree = TreeOf(Files(("a.txt", "a1\n"), ("b.txt", "b2\n")));
        var unionFiles = Files(("a.txt", "a2\n"), ("b.txt", "b2\n"));
        var a2 = await CommitAsync("A2 merges B1", [a1.Id, b1.Id], a1Tree, unionFiles, 1_700_000_300);
        var b2 = await CommitAsync("B2 merges A1", [b1.Id, a1.Id], b1Tree, unionFiles, 1_700_000_400);

        var response = await client.GetAsync($"/rpc/merge-base?first={a2.Id}&second={b2.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Equal shadows (both dominate only the root) tie-break on the smaller content address.
        var expected = StringComparer.Ordinal.Compare(a1.Id, b1.Id) < 0 ? a1.Id : b1.Id;
        Assert.Equal(expected, (await ReadJsonAsync(response)).GetProperty("mergeBase").GetString());
    }

    [Fact]
    public async Task MergeBase_UnknownChangeset_Is404()
    {
        var ghost = Sha256.HashHex(Encoding.UTF8.GetBytes("a changeset no one created"));

        var response = await client.GetAsync($"/rpc/merge-base?first={ghost}&second={ghost}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal(ghost, problem.GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task MergeBase_MissingQuery_Is400()
    {
        var response = await client.GetAsync("/rpc/merge-base");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    // ---- Merge -----------------------------------------------------------------------------

    [Fact]
    public async Task Merge_CleanSides_CreatesMergeChangesetWithBothParents()
    {
        var rootFiles = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000, RefMove("main", RefStore.CreationRevision));
        var ours = await CommitAsync("ours edits a", [root.Id], rootTree, Files(("a.txt", "a2\n"), ("b.txt", "b1\n")), 1_700_000_100);
        var theirs = await CommitAsync("theirs adds c", [root.Id], rootTree, Files(("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c\n")), 1_700_000_200);

        var response = await PostAsync("/rpc/merge", new
        {
            ours = ours.Id,
            theirs = theirs.Id,
            message = "merge theirs into ours",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_300L,
            refMove = new { name = "main", expectedRevision = 1L },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(root.Id, body.GetProperty("mergeBase").GetString());
        Assert.Empty(body.GetProperty("conflicts").EnumerateArray());

        // The merge changeset: both parents, delta relative to the first parent only.
        var mergeId = body.GetProperty("id").GetString()!;
        var log = factory.Services.GetRequiredService<ChangesetLog>();
        Assert.True(log.TryGetById(mergeId, out var mergeChangeset));
        Assert.Equal(2, mergeChangeset!.Parents.Length);
        Assert.Equal(ours.Id, mergeChangeset.Parents[0]);
        Assert.Equal(theirs.Id, mergeChangeset.Parents[1]);
        var change = Assert.Single(mergeChangeset.Changes);
        Assert.Equal("c.txt", change.Path);
        Assert.Equal(ChangeType.Added, change.Type);

        // The merged tree composes both sides, and the delta's content is stored.
        var tree = TreeAt(mergeId);
        Assert.Equal(BlobOf("a2\n"), TreeEntryAt(tree, "a.txt"));
        Assert.Equal(BlobOf("b1\n"), TreeEntryAt(tree, "b.txt"));
        Assert.Equal(BlobOf("c\n"), TreeEntryAt(tree, "c.txt"));
        Assert.True(factory.Services.GetRequiredService<BlobStore>().Exists(BlobOf("c\n")));

        // The ref move published the merge commit.
        Assert.Equal(mergeId, body.GetProperty("ref").GetProperty("target").GetString());
        Assert.Equal(2, body.GetProperty("ref").GetProperty("revision").GetInt64());
        Assert.Equal(mergeId, factory.Services.GetRequiredService<RefStore>().Get("main")!.Target);
    }

    [Fact]
    public async Task Merge_ConflictedContent_Answers409WithMarkersAndAppendsNothing()
    {
        var rootFiles = Files(("f.txt", "base line\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var ours = await CommitAsync("ours edits f", [root.Id], rootTree, Files(("f.txt", "ours line\n")), 1_700_000_100);
        var theirs = await CommitAsync("theirs edits f", [root.Id], rootTree, Files(("f.txt", "theirs line\n")), 1_700_000_200);

        var response = await PostAsync("/rpc/merge", new
        {
            ours = ours.Id,
            theirs = theirs.Id,
            message = "conflicted merge",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_300L,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(root.Id, body.GetProperty("mergeBase").GetString());
        Assert.True(body.GetProperty("id").ValueKind is JsonValueKind.Null);

        // The conflict carries the kind, both sides' blobs, and the conflict-marked merged text.
        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("f.txt", conflict.GetProperty("path").GetString());
        Assert.Equal("content", conflict.GetProperty("kind").GetString());
        Assert.Equal(BlobOf("base line\n"), conflict.GetProperty("baseBlob").GetString());
        Assert.Equal(BlobOf("ours line\n"), conflict.GetProperty("oursBlob").GetString());
        Assert.Equal(BlobOf("theirs line\n"), conflict.GetProperty("theirsBlob").GetString());
        var markers = conflict.GetProperty("mergedContent").GetString()!;
        Assert.Contains("<<<<<<<", markers);
        Assert.Contains("=======", markers);
        Assert.Contains(">>>>>>>", markers);
        Assert.Contains("ours line", markers);
        Assert.Contains("theirs line", markers);

        // Nothing was appended: the merged tree is not committable until the conflicts resolve.
        Assert.Equal(3, factory.Services.GetRequiredService<ChangesetLog>().Count);
    }

    [Fact]
    public async Task Merge_DisjointHistories_Is400()
    {
        var first = await CommitAsync("first root", [], TreeSnapshot.Empty, Files(("a.txt", "a\n")), 1_700_000_000);
        var second = await CommitAsync("second root", [], TreeSnapshot.Empty, Files(("z.txt", "z\n")), 1_700_000_100);

        var response = await PostAsync("/rpc/merge", new
        {
            ours = first.Id,
            theirs = second.Id,
            message = "nothing shares history",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_200L,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Contains("disjoint", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Merge_UnknownTip_Is404()
    {
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, Files(("a.txt", "a\n")), 1_700_000_000);
        var ghost = Sha256.HashHex(Encoding.UTF8.GetBytes("a changeset no one created"));

        var response = await PostAsync("/rpc/merge", new
        {
            ours = root.Id,
            theirs = ghost,
            message = "theirs is a ghost",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_100L,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("changeset", problem.GetProperty("objectType").GetString());
        Assert.Equal(ghost, problem.GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task Merge_MissingMessage_Is400()
    {
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, Files(("a.txt", "a\n")), 1_700_000_000);

        var response = await PostAsync("/rpc/merge", new
        {
            ours = root.Id,
            theirs = root.Id,
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_100L,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, factory.Services.GetRequiredService<ChangesetLog>().Count);
    }

    [Fact]
    public async Task Merge_StaleRefMove_Returns409WithCurrentRefValueWhileTheChangesetStaysAppended()
    {
        var rootFiles = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000, RefMove("main", RefStore.CreationRevision));
        var ours = await CommitAsync("ours edits a", [root.Id], rootTree, Files(("a.txt", "a2\n"), ("b.txt", "b1\n")), 1_700_000_100);
        var theirs = await CommitAsync("theirs adds c", [root.Id], rootTree, Files(("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c\n")), 1_700_000_200);

        var firstMerge = await PostAsync("/rpc/merge", new
        {
            ours = ours.Id,
            theirs = theirs.Id,
            message = "first merge",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_300L,
            refMove = new { name = "main", expectedRevision = 1L },
        });
        Assert.Equal(HttpStatusCode.Created, firstMerge.StatusCode);
        var firstMergeId = (await ReadJsonAsync(firstMerge)).GetProperty("id").GetString()!;

        // A second, clean merge whose client still believes main is at revision 1.
        var ours2 = await CommitAsync("ours edits a again", [ours.Id], TreeOf(Files(("a.txt", "a2\n"), ("b.txt", "b1\n"))), Files(("a.txt", "a3\n"), ("b.txt", "b1\n")), 1_700_000_400);

        var staleMerge = await PostAsync("/rpc/merge", new
        {
            ours = ours2.Id,
            theirs = theirs.Id,
            message = "second merge",
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_500L,
            refMove = new { name = "main", expectedRevision = 1L },
        });

        Assert.Equal(HttpStatusCode.Conflict, staleMerge.StatusCode);
        var problem = await ReadJsonAsync(staleMerge);
        Assert.Equal("ref_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(firstMergeId, problem.GetProperty("currentTarget").GetString());
        Assert.Equal(2, problem.GetProperty("currentRevision").GetInt64());

        // Append and ref move are decoupled: the merge changeset is stored, unreachable until
        // a rebased retry or another commit references it.
        Assert.Equal(6, factory.Services.GetRequiredService<ChangesetLog>().Count);
        Assert.Equal(firstMergeId, factory.Services.GetRequiredService<RefStore>().Get("main")!.Target);
    }

    [Fact]
    public async Task Merge_RequiresBearerToken()
    {
        using var anonymous = factory.CreateClient();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await anonymous.PostAsync("/rpc/merge", body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Rebase ----------------------------------------------------------------------------

    [Fact]
    public async Task Rebase_ReplaysTheChainOntoTheNewBase()
    {
        var rootFiles = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000, RefMove("main", RefStore.CreationRevision));
        var newBase = await CommitAsync("base edits a", [root.Id], rootTree, Files(("a.txt", "a2\n"), ("b.txt", "b1\n")), 1_700_000_100);
        var f1Files = Files(("a.txt", "a1\n"), ("b.txt", "b1\n"), ("feature.txt", "f1\n"));
        var f1Tree = TreeOf(f1Files);
        var f1 = await CommitAsync("add feature", [root.Id], rootTree, f1Files, 1_700_000_200);
        var f2 = await CommitAsync("edit feature", [f1.Id], f1Tree, Files(("a.txt", "a1\n"), ("b.txt", "b1\n"), ("feature.txt", "f2\n")), 1_700_000_300);

        var response = await PostAsync("/rpc/rebase", new
        {
            start = f1.Id,
            tip = f2.Id,
            newBase = newBase.Id,
            refMove = new { name = "main", expectedRevision = 1L },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Empty(body.GetProperty("conflicts").EnumerateArray());

        // One rebased changeset per source changeset, first-parent linked onto the new base,
        // message and author carrying over.
        var rebased = body.GetProperty("rebased");
        Assert.Equal(2, rebased.GetArrayLength());
        Assert.Equal(f1.Id, rebased[0].GetProperty("sourceId").GetString());
        Assert.Equal(f2.Id, rebased[1].GetProperty("sourceId").GetString());
        var r1 = rebased[0].GetProperty("id").GetString()!;
        var r2 = rebased[1].GetProperty("id").GetString()!;

        var log = factory.Services.GetRequiredService<ChangesetLog>();
        Assert.True(log.TryGetById(r1, out var rebased1));
        Assert.Equal(newBase.Id, rebased1!.Parents[0]);
        Assert.Equal("add feature", rebased1.Message);
        Assert.Equal(TestAuthor, rebased1.Author);
        Assert.True(log.TryGetById(r2, out var rebased2));
        Assert.Equal(r1, rebased2!.Parents[0]);
        Assert.Equal("edit feature", rebased2.Message);

        // The replayed chain's final tree composes the new base and the feature work.
        var tree = TreeAt(r2);
        Assert.Equal(BlobOf("a2\n"), TreeEntryAt(tree, "a.txt"));
        Assert.Equal(BlobOf("b1\n"), TreeEntryAt(tree, "b.txt"));
        Assert.Equal(BlobOf("f2\n"), TreeEntryAt(tree, "feature.txt"));

        // The ref move published the rebased tip.
        Assert.Equal(r2, body.GetProperty("ref").GetProperty("target").GetString());
        Assert.Equal(r2, factory.Services.GetRequiredService<RefStore>().Get("main")!.Target);
    }

    [Fact]
    public async Task Rebase_ComposingBothSideEdits_StoresTheSynthesizedBlob()
    {
        // The new base appends a line; the feature chain prepends one; the replay's line-level
        // merge composes both — content neither side had, which the server must store before the
        // rebased changeset that references it.
        var rootFiles = Files(("x.txt", "one\ntwo\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var newBase = await CommitAsync("base appends", [root.Id], rootTree, Files(("x.txt", "one\ntwo\nthree\n")), 1_700_000_100);
        var f1 = await CommitAsync("feature prepends", [root.Id], rootTree, Files(("x.txt", "zero\none\ntwo\n")), 1_700_000_200);

        var response = await PostAsync("/rpc/rebase", new { start = f1.Id, tip = f1.Id, newBase = newBase.Id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Empty(body.GetProperty("conflicts").EnumerateArray());

        var composed = BlobOf("zero\none\ntwo\nthree\n");
        var store = factory.Services.GetRequiredService<BlobStore>();
        Assert.True(store.Exists(composed));
        var r1 = body.GetProperty("rebased")[0].GetProperty("id").GetString()!;
        Assert.Equal(composed, TreeEntryAt(TreeAt(r1), "x.txt"));
    }

    [Fact]
    public async Task Rebase_ConflictedPaths_AppendsTheChainAndCarriesMarkers()
    {
        var rootFiles = Files(("f.txt", "a\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var newBase = await CommitAsync("base edits f", [root.Id], rootTree, Files(("f.txt", "base\n")), 1_700_000_100);
        var f1 = await CommitAsync("feature edits f", [root.Id], rootTree, Files(("f.txt", "feature\n")), 1_700_000_200);

        var response = await PostAsync("/rpc/rebase", new { start = f1.Id, tip = f1.Id, newBase = newBase.Id });

        // Replay never aborts: the chain is appended either way, conflicts attached.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(0, conflict.GetProperty("sourceIndex").GetInt32());
        Assert.Equal(f1.Id, conflict.GetProperty("sourceId").GetString());
        var r1 = conflict.GetProperty("rebasedId").GetString()!;

        var inner = conflict.GetProperty("conflict");
        Assert.Equal("f.txt", inner.GetProperty("path").GetString());
        Assert.Equal("content", inner.GetProperty("kind").GetString());
        Assert.Equal(BlobOf("a\n"), inner.GetProperty("baseBlob").GetString());
        Assert.Equal(BlobOf("base\n"), inner.GetProperty("oursBlob").GetString()); // ours is the new base
        Assert.Equal(BlobOf("feature\n"), inner.GetProperty("theirsBlob").GetString());
        var markers = inner.GetProperty("mergedContent").GetString()!;
        Assert.Contains("<<<<<<<", markers);
        Assert.Contains("=======", markers);
        Assert.Contains(">>>>>>>", markers);

        // The rebased changeset was appended (message carried over) and its tree keeps the new
        // base's state on the conflicted path — the resolution continues forward from there.
        var log = factory.Services.GetRequiredService<ChangesetLog>();
        Assert.Equal(4, log.Count);
        Assert.True(log.TryGetById(r1, out var rebased1));
        Assert.Equal("feature edits f", rebased1!.Message);
        Assert.Equal(BlobOf("base\n"), TreeEntryAt(TreeAt(r1), "f.txt"));
    }

    [Fact]
    public async Task Rebase_TipNotReachableFromStart_Is400()
    {
        var rootFiles = Files(("a.txt", "a1\n"));
        var rootTree = TreeOf(rootFiles);
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, rootFiles, 1_700_000_000);
        var baseBranch = await CommitAsync("base branch", [root.Id], rootTree, Files(("a.txt", "a2\n")), 1_700_000_100);
        var featureBranch = await CommitAsync("feature branch", [root.Id], rootTree, Files(("a.txt", "a3\n")), 1_700_000_200);

        var response = await PostAsync("/rpc/rebase", new { start = baseBranch.Id, tip = featureBranch.Id, newBase = root.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Contains("not reachable", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Rebase_UnknownNewBase_Is404()
    {
        var root = await CommitAsync("root", [], TreeSnapshot.Empty, Files(("a.txt", "a\n")), 1_700_000_000);
        var ghost = Sha256.HashHex(Encoding.UTF8.GetBytes("a changeset no one created"));

        var response = await PostAsync("/rpc/rebase", new { start = root.Id, tip = root.Id, newBase = ghost });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    // ---- Harness ---------------------------------------------------------------------------

    private sealed record FileSpec(string Path, string Content);

    private static FileSpec[] Files(params (string Path, string Content)[] files)
    {
        return [.. files.Select(file => new FileSpec(file.Path, file.Content))];
    }

    private static string BlobOf(string content)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(content)).Hash;
    }

    private static TreeSnapshot TreeOf(FileSpec[] files)
    {
        return new TreeSnapshot(files.ToImmutableSortedDictionary(
            file => file.Path,
            file => new TreeEntry(BlobOf(file.Content), FileContentKind.Regular),
            StringComparer.Ordinal));
    }

    private static string TreeEntryAt(TreeSnapshot tree, string path)
    {
        Assert.True(tree.TryGet(path, out var entry), $"expected '{path}' in the tree");
        return entry!.BlobHash;
    }

    /// <summary>Commits a changeset through the real pipeline, shipping every file's content inline.</summary>
    private async Task<Changeset> CommitAsync(
        string message,
        string[] parents,
        TreeSnapshot baseTree,
        FileSpec[] tree,
        long timestamp,
        CreateChangesetRequest.RefMoveRequest? refMove = null)
    {
        var changes = TreeDiff.Diff(baseTree, TreeOf(tree)).ToArray();
        var changeset = Changeset.Create([.. parents], [.. changes], message, TestAuthor, timestamp);
        var blobs = tree.Select(file => new CreateChangesetRequest.BlobPayload(BlobOf(file.Content), Encoding.UTF8.GetBytes(file.Content))).ToList();

        var response = await PostChangesetAsync(changeset, blobs, refMove);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset;
    }

    private async Task<HttpResponseMessage> PostChangesetAsync(
        Changeset changeset,
        List<CreateChangesetRequest.BlobPayload>? blobs = null,
        CreateChangesetRequest.RefMoveRequest? refMove = null)
    {
        var payload = new
        {
            id = changeset.Id,
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

    private async Task<HttpResponseMessage> PostAsync(string uri, object payload)
    {
        using var body = new StringContent(JsonSerializer.Serialize(payload, RequestJson), Encoding.UTF8, "application/json");
        return await client.PostAsync(uri, body);
    }

    private static CreateChangesetRequest.RefMoveRequest RefMove(string name, long expectedRevision)
    {
        return new CreateChangesetRequest.RefMoveRequest(name, expectedRevision);
    }

    private TreeSnapshot TreeAt(string changesetId)
    {
        var engine = factory.Services.GetRequiredService<DerivedDataEngine>();
        return TreeSnapshotDerivedData.Decode(engine.Get(TreeSnapshotDerivedData.TypeName, changesetId));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        document.Dispose();
        return root;
    }
}
