using System.Net;
using System.Text;
using System.Text.Json;
using Scion.Core;
using Scion.Core.Storage;
using Scion.Server.Endpoints;
using Scion.Server.Errors;

namespace Scion.Server.Tests;

/// <summary>
/// The agent-API contract, executed (spec D5, parity tier): every endpoint the quickstart
/// (docs/agent-api.md) documents is driven through the real pipeline here, asserting the exact
/// statuses, content types, body shapes, and error codes the document promises. The seed helper
/// (<see cref="ScionRepository"/>) walks the quickstart's own loop — upload blobs, commit with a
/// CAS ref move, query server-side — over plain HTTP, so a change that breaks either the behavior
/// or the document fails here.
/// </summary>
public sealed class AgentApiParityTests : IDisposable
{
    private static readonly Author TestAuthor = new("Agent", "agent@scion.test");

    private readonly ScionServerFactory factory;
    private readonly HttpClient client;
    private readonly ScionRepository repo;

    public AgentApiParityTests()
    {
        factory = new ScionServerFactory();
        client = factory.CreateAuthedClient();
        repo = new ScionRepository(client);
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    // ── GET /healthz ── the liveness probe the quickstart starts from ───────────────────────────

    [Fact]
    public async Task Healthz_IsOkWithTheDocumentedBody()
    {
        var response = await repo.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.JsonAsync();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    // ── POST /rpc/blobs/{hash} ── the commit step's content upload ──────────────────────────────

    [Fact]
    public async Task BlobUpload_NewContent_IsCreatedWithTheStoredAddress()
    {
        var content = Encoding.UTF8.GetBytes("docs: the beginning\n");

        var response = await PostBlobAsync(content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Sha256.HashHex(content), (await response.JsonAsync()).GetProperty("hash").GetString());
    }

    [Fact]
    public async Task BlobUpload_ExistingContent_IsOkTheSecondTime()
    {
        var content = Encoding.UTF8.GetBytes("docs: the beginning\n");

        var first = await PostBlobAsync(content);
        var second = await PostBlobAsync(content);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task BlobUpload_ContentMismatchingAddress_IsValidation()
    {
        var content = Encoding.UTF8.GetBytes("the content");

        var response = await client.PostAsync($"/rpc/blobs/{Sha256.HashHex("another content"u8.ToArray())}", new ByteArrayContent(content));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task BlobUpload_MalformedAddress_IsValidation()
    {
        var response = await client.PostAsync("/rpc/blobs/not-a-hash", new ByteArrayContent([1]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    // ── POST /rpc/changesets ── the commit step's append ────────────────────────────────────────

    [Fact]
    public async Task Commit_TwoCommitsFollowTheQuickstartLoop()
    {
        var root = await repo.CommitAsync(
            "initial commit",
            [FileChange.Add("docs/readme.md", "docs: the beginning\n")],
            refMove: ("main", RefStore.CreationRevision));

        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var rootBody = await root.JsonAsync();
        Assert.Equal(64, rootBody.GetProperty("id").GetString()!.Length);
        // No ref move was declined here — the creation move won; the response carries the new ref state.
        Assert.Equal("main", rootBody.GetProperty("ref").GetProperty("name").GetString());
        Assert.Equal(rootBody.GetProperty("id").GetString(), rootBody.GetProperty("ref").GetProperty("target").GetString());
        Assert.Equal(1, rootBody.GetProperty("ref").GetProperty("revision").GetInt64());

        var rootId = rootBody.GetProperty("id").GetString()!;
        var child = await repo.CommitAsync(
            "extend readme",
            [FileChange.Modify("docs/readme.md", "docs: the beginning\n", "docs: the continuation\n")],
            parents: [rootId],
            refMove: ("main", 1));

        Assert.Equal(HttpStatusCode.Created, child.StatusCode);
        var childBody = await child.JsonAsync();
        Assert.Equal(2, childBody.GetProperty("ref").GetProperty("revision").GetInt64());
        Assert.Equal(childBody.GetProperty("id").GetString(), childBody.GetProperty("ref").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Commit_LyingId_IsValidation()
    {
        var content = Encoding.UTF8.GetBytes("a commit whose claimed id is a lie");
        var changeset = NewChangeset(
            "lying about the id",
            [new ChangeEntry("file.txt", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            []);
        var wrongId = Sha256.HashHex("some other payload"u8.ToArray());

        var response = await PostChangesetAsync(changeset, wrongId, [content]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Commit_StaleRefMove_IsConflictCarryingTheCurrentValue_AndTheCommitStillLands()
    {
        var root = await CommitRootAsync();
        var child = await CommitChildAsync(root);

        var stale = NewChangeset(
            "racing commit",
            [new ChangeEntry("docs/race.md", null, Blob.Create(("racing"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Added)],
            [child]);
        var response = await PostChangesetAsync(
            stale,
            refMove: new CreateChangesetRequest.RefMoveRequest("main", 1),
            contents: ["racing"u8.ToArray()]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.Equal("ref_conflict", problem.Code());
        Assert.Equal("main", problem.GetProperty("ref").GetString());
        Assert.Equal(child, problem.GetProperty("currentTarget").GetString());
        Assert.Equal(2, problem.GetProperty("currentRevision").GetInt64());

        // The changeset is stored even though the move lost: re-read it by id (the documented
        // recovery) — 200, not 404.
        var stored = await repo.GetAsync($"/rpc/changesets/{stale.Id}");
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
    }

    [Fact]
    public async Task Commit_ReferencingUnstoredBlob_IsValidation()
    {
        var absent = Blob.Create(("never uploaded"u8).ToArray());
        var changeset = NewChangeset(
            "references nothing",
            [new ChangeEntry("file.txt", null, absent.Hash, FileContentKind.Regular, ChangeType.Added)],
            []);

        var response = await PostChangesetAsync(changeset);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Commit_ModifyingAPathTheParentNeverHad_IsValidation_AndNothingAppends()
    {
        // A Modified entry assumes the path exists in the first parent's snapshot. Accepting one
        // that does not would leave the tree at this changeset uncomputable — every later read
        // (diff, merge, checkout) would fail — so create rejects it with the stable validation
        // problem and the log stays clean.
        var root = await CommitRootAsync();
        var contents = "dangling modification"u8.ToArray();
        var changeset = NewChangeset(
            "modifies a path no ancestor added",
            [new ChangeEntry("docs/absent.md", Blob.Create(contents).Hash, Blob.Create(contents).Hash, FileContentKind.Regular, ChangeType.Modified)],
            [root]);

        var response = await PostChangesetAsync(changeset, contents: [contents]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());

        // The poisoned changeset never entered the log: its id answers 404, not a 500.
        var stored = await repo.GetAsync($"/rpc/changesets/{changeset.Id}");
        Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task Commit_MalformedJsonBody_IsBadRequestWithTheDocumentedCode(string body)
    {
        var response = await repo.PostJsonAsync("/rpc/changesets", body);

        await AssertMalformedBodyAsync(response);
    }

    // ── GET /rpc/changesets/{id} ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChangeset_ReturnsTheStoredRecord()
    {
        var root = await CommitRootAsync();

        var response = await repo.GetAsync($"/rpc/changesets/{root}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(root, body.GetProperty("id").GetString());
        Assert.Equal("initial commit", body.GetProperty("message").GetString());
        Assert.Equal("Agent", body.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("added", body.GetProperty("changes")[0].GetProperty("type").GetString());
        Assert.Equal("regular", body.GetProperty("changes")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GetChangeset_UnknownId_IsNotFoundWithObjectDetails()
    {
        var response = await repo.GetAsync($"/rpc/changesets/{UnknownId('a')}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("not_found", body.Code());
        Assert.Equal("changeset", body.GetProperty("objectType").GetString());
        Assert.Equal(UnknownId('a'), body.GetProperty("objectId").GetString());
    }

    // ── GET /rpc/log ── the agent's history read, paged, zero local walk ────────────────────────

    [Fact]
    public async Task Log_EmptyLog_IsAnEmptyPage()
    {
        var response = await repo.GetAsync("/rpc/log");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Empty(body.GetProperty("changesets").EnumerateArray());
        Assert.True(body.GetProperty("nextAfter").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Log_PagesFirstParentHistoryNewestFirst_WithCursor()
    {
        var (root, tip) = await SeedTwoCommitLineageAsync();

        var first = await repo.GetAsync("/rpc/log?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.JsonAsync();
        Assert.Single(firstBody.GetProperty("changesets").EnumerateArray());
        Assert.Equal(tip, firstBody.GetProperty("changesets")[0].GetProperty("id").GetString());
        // The cursor is the page's last entry; sending it back as `after` resumes at its first parent.
        Assert.Equal(tip, firstBody.GetProperty("nextAfter").GetString());

        var cursor = firstBody.GetProperty("nextAfter").GetString();
        var second = await repo.GetAsync($"/rpc/log?limit=1&after={cursor}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.JsonAsync();
        Assert.Single(secondBody.GetProperty("changesets").EnumerateArray());
        Assert.Equal(root, secondBody.GetProperty("changesets")[0].GetProperty("id").GetString());
        Assert.True(secondBody.GetProperty("nextAfter").ValueKind is JsonValueKind.Null);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("many")]
    public async Task Log_InvalidLimit_IsValidation(string limit)
    {
        var response = await repo.GetAsync($"/rpc/log?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Log_UnknownTip_IsNotFound()
    {
        var response = await repo.GetAsync($"/rpc/log?tip={UnknownId('b')}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    // ── GET /rpc/path-history ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PathHistory_ReturnsTouchingChangesetsOldestFirst()
    {
        var (root, tip) = await SeedTwoCommitLineageAsync();

        var response = await repo.GetAsync($"/rpc/path-history?changeset={tip}&path=docs/readme.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("docs/readme.md", body.GetProperty("path").GetString());
        var changesets = body.GetProperty("changesets");
        Assert.Equal(2, changesets.GetArrayLength());
        Assert.Equal(root, changesets[0].GetString());
        Assert.Equal(tip, changesets[1].GetString());
    }

    [Fact]
    public async Task PathHistory_MissingQuery_IsValidation()
    {
        var response = await repo.GetAsync("/rpc/path-history?path=docs/readme.md");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task PathHistory_UnknownChangeset_IsNotFound()
    {
        var response = await repo.GetAsync($"/rpc/path-history?changeset={UnknownId('c')}&path=docs/readme.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    // ── GET /rpc/diff ── server-side delta between any two changesets ───────────────────────────

    [Fact]
    public async Task Diff_BetweenTwoChangesets_IsTheChangedPathDelta()
    {
        var (root, tip) = await SeedTwoCommitLineageAsync();

        var response = await repo.GetAsync($"/rpc/diff?from={root}&to={tip}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(root, body.GetProperty("from").GetString());
        Assert.Equal(tip, body.GetProperty("to").GetString());
        var changes = body.GetProperty("changes");
        Assert.Single(changes.EnumerateArray());
        Assert.Equal("docs/readme.md", changes[0].GetProperty("path").GetString());
        Assert.Equal("modified", changes[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Diff_MissingQuery_IsValidation()
    {
        var response = await repo.GetAsync("/rpc/diff?from=x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Diff_UnknownTip_IsNotFound()
    {
        var (root, _) = await SeedTwoCommitLineageAsync();

        var response = await repo.GetAsync($"/rpc/diff?from={root}&to={UnknownId('d')}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    // ── GET /rpc/blame ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Blame_AttributesEveryLineToItsIntroducingChangeset()
    {
        var (root, tip) = await SeedTwoCommitLineageAsync();

        var response = await repo.GetAsync($"/rpc/blame?changeset={tip}&path=docs/readme.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(tip, body.GetProperty("changeset").GetString());
        Assert.Equal("docs/readme.md", body.GetProperty("path").GetString());
        var lines = body.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(1, lines[0].GetProperty("line").GetInt32());
        Assert.Equal(root, lines[0].GetProperty("changesetId").GetString());
        Assert.Equal("docs: the beginning\n", lines[0].GetProperty("content").GetString());
        Assert.Equal(2, lines[1].GetProperty("line").GetInt32());
        Assert.Equal(tip, lines[1].GetProperty("changesetId").GetString());
        Assert.Equal("docs: the continuation\n", lines[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Blame_PathAbsentAtChangeset_IsNotFound()
    {
        var (root, _) = await SeedTwoCommitLineageAsync();

        var response = await repo.GetAsync($"/rpc/blame?changeset={root}&path=docs/absent.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    // ── GET /rpc/merge-base ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MergeBase_OfDivergedTips_IsTheCommonAncestor()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await repo.GetAsync($"/rpc/merge-base?first={seed.mainTip}&second={seed.branchTip}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(seed.root, (await response.JsonAsync()).GetProperty("mergeBase").GetString());
    }

    [Fact]
    public async Task MergeBase_OfDisjointHistories_IsANullAnswer_NotAnError()
    {
        var seed = await SeedDivergedBranchesAsync();
        var orphan = await CommitOrphanRootAsync();

        var response = await repo.GetAsync($"/rpc/merge-base?first={seed.mainTip}&second={orphan}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("mergeBase").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task MergeBase_MissingQuery_IsValidation()
    {
        var response = await repo.GetAsync("/rpc/merge-base?first=x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    // ── POST /rpc/merge ── the merge runs server-side; result is new changesets, nothing to push ─

    [Fact]
    public async Task Merge_Clean_AppendsAQueryableMergeChangeset()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await MergeAsync(ours: seed.mainTip, theirs: seed.branchAdditive, message: "merge feature");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(seed.root, body.GetProperty("mergeBase").GetString());
        Assert.False(body.GetProperty("id").ValueKind is JsonValueKind.Null);
        Assert.Empty(body.GetProperty("conflicts").EnumerateArray());

        // The merge changeset is a real, queryable changeset: two parents, first is ours.
        var fetched = await repo.GetAsync($"/rpc/changesets/{body.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var mergeRecord = await fetched.JsonAsync();
        Assert.Equal(2, mergeRecord.GetProperty("parents").GetArrayLength());
        Assert.Equal(seed.mainTip, mergeRecord.GetProperty("parents")[0].GetString());
        Assert.Equal(seed.branchAdditive, mergeRecord.GetProperty("parents")[1].GetString());
    }

    [Fact]
    public async Task Merge_Conflicted_IsConflict_WithTheResolutionVocabulary_AndNothingAppended()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await MergeAsync(ours: seed.mainTip, theirs: seed.branchTip, message: "merge conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(seed.root, body.GetProperty("mergeBase").GetString());
        Assert.True(body.GetProperty("id").ValueKind is JsonValueKind.Null);
        var conflicts = body.GetProperty("conflicts");
        Assert.Single(conflicts.EnumerateArray());
        Assert.Equal("docs/shared.md", conflicts[0].GetProperty("path").GetString());
        Assert.Equal("content", conflicts[0].GetProperty("kind").GetString());
        Assert.Contains("<<<<<<<", conflicts[0].GetProperty("mergedContent").GetString());
    }

    [Fact]
    public async Task Merge_MissingAuthor_IsValidation()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await repo.PostJsonAsync("/rpc/merge", new
        {
            ours = seed.mainTip,
            theirs = seed.branchTip,
            message = "no author",
            timestampUtc = 1_700_000_500,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Merge_UnknownTip_IsNotFound()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await MergeAsync(ours: seed.mainTip, theirs: UnknownId('e'), message: "merging nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task Merge_MalformedJsonBody_IsBadRequestWithTheDocumentedCode(string body)
    {
        var response = await repo.PostJsonAsync("/rpc/merge", body);

        await AssertMalformedBodyAsync(response);
    }

    // ── POST /rpc/rebase ── replay never aborts; 201 with conflicts riding along ─────────────────

    [Fact]
    public async Task Rebase_ReplaysTheChainCleanly()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await RebaseAsync(start: seed.branchAdditive, tip: seed.branchAdditive, newBase: seed.mainTip);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(seed.mainTip, body.GetProperty("newBase").GetString());
        var rebased = body.GetProperty("rebased");
        Assert.Single(rebased.EnumerateArray());
        Assert.Equal(seed.branchAdditive, rebased[0].GetProperty("sourceId").GetString());
        Assert.False(rebased[0].GetProperty("id").ValueKind is JsonValueKind.Null);
        Assert.Empty(body.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task Rebase_ConflictedPathsKeepTheNewBase_AndTheChainStillLands201()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await RebaseAsync(start: seed.branchAdditive, tip: seed.branchTip, newBase: seed.mainTip);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(2, body.GetProperty("rebased").GetArrayLength());
        Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(1, body.GetProperty("conflicts")[0].GetProperty("sourceIndex").GetInt32());
        Assert.Equal(seed.branchTip, body.GetProperty("conflicts")[0].GetProperty("sourceId").GetString());
        Assert.Equal("docs/shared.md", body.GetProperty("conflicts")[0].GetProperty("conflict").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Rebase_TipNotExtendingStart_IsValidation()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await RebaseAsync(start: seed.mainTip, tip: seed.branchTip, newBase: seed.mainTip);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task Rebase_UnknownChain_IsNotFound()
    {
        var seed = await SeedDivergedBranchesAsync();

        var response = await RebaseAsync(start: UnknownId('f'), tip: seed.branchTip, newBase: seed.mainTip);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task Rebase_MalformedJsonBody_IsBadRequestWithTheDocumentedCode(string body)
    {
        var response = await repo.PostJsonAsync("/rpc/rebase", body);

        await AssertMalformedBodyAsync(response);
    }

    // ── POST /rpc/trees/missing ── the fetch protocol's discovery half ──────────────────────────

    [Fact]
    public async Task TreesMissing_ReportsPresentAndMissingForTheWantedPaths()
    {
        var (_, tip) = await SeedTwoCommitLineageAsync();

        var response = await PostTreesMissingAsync(new { changesetId = tip, wantedPaths = new[] { "docs/**" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(64, body.GetProperty("rootTree").GetString()!.Length);
        // docs/** at the tip covers both docs files the root commit added (readme and shared).
        Assert.Equal(2, body.GetProperty("presentBlobs").GetArrayLength());
        Assert.Empty(body.GetProperty("missingBlobs").EnumerateArray());
    }

    [Fact]
    public async Task TreesMissing_ByRootTreeHash_AnswersTheSameContract()
    {
        var (_, tip) = await SeedTwoCommitLineageAsync();
        var discovered = await (await PostTreesMissingAsync(new { changesetId = tip, wantedPaths = new[] { "docs/**" } }))
            .JsonAsync();
        var rootTree = discovered.GetProperty("rootTree").GetString();

        var response = await PostTreesMissingAsync(new { rootTree, wantedPaths = new[] { "docs/**" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rootTree, (await response.JsonAsync()).GetProperty("rootTree").GetString());
    }

    [Fact]
    public async Task TreesMissing_NamingBothRoots_IsValidation()
    {
        var (_, tip) = await SeedTwoCommitLineageAsync();

        var response = await PostTreesMissingAsync(new { changesetId = tip, rootTree = UnknownId('1'), wantedPaths = new[] { "docs/**" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task TreesMissing_UnknownChangeset_IsNotFound()
    {
        var response = await PostTreesMissingAsync(new { changesetId = UnknownId('2') });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task TreesMissing_MalformedJsonBody_IsBadRequestWithTheDocumentedCode()
    {
        var response = await repo.PostJsonAsync("/rpc/trees/missing", "{ not json");

        await AssertMalformedBodyAsync(response);
    }

    // ── POST /rpc/blobs/missing ── the fetch protocol's transfer half ───────────────────────────

    [Fact]
    public async Task BlobsMissing_StreamsFramedEntriesForEveryRequestedHash()
    {
        var first = "docs: the beginning\n"u8.ToArray();
        var second = "docs: the continuation\n"u8.ToArray();
        await PostBlobAsync(first);
        await PostBlobAsync(second);

        var response = await PostBlobsMissingAsync([Sha256.HashHex(first), Sha256.HashHex(second), Sha256.HashHex(first)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);

        // The duplicate is served once: one framed entry per distinct hash, in request order.
        var entries = BlobStreamFraming.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, entries.Count);
        Assert.Equal(Sha256.HashHex(first), entries[0].Key);
        Assert.Equal(first, entries[0].Content);
        Assert.Equal(Sha256.HashHex(second), entries[1].Key);
        Assert.Equal(second, entries[1].Content);
    }

    [Fact]
    public async Task BlobsMissing_UnknownBlob_Is404BeforeAnyStream()
    {
        var stored = "stored content\n"u8.ToArray();
        await PostBlobAsync(stored);

        var response = await PostBlobsMissingAsync([Sha256.HashHex(stored), UnknownId('9')]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonAsync();
        Assert.Equal("not_found", body.Code());
        Assert.Equal("blob", body.GetProperty("objectType").GetString());
    }

    [Fact]
    public async Task BlobsMissing_MalformedHash_IsValidation()
    {
        var response = await PostBlobsMissingAsync(["short"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.JsonAsync()).Code());
    }

    [Fact]
    public async Task BlobsMissing_MalformedJsonBody_IsBadRequestWithTheDocumentedCode()
    {
        var response = await repo.PostJsonAsync("/rpc/blobs/missing", "{ not json");

        await AssertMalformedBodyAsync(response);
    }

    // ── Authentication ── every /rpc endpoint is bearer-gated; /healthz is not ──────────────────

    [Fact]
    public async Task EveryRpcEndpoint_RequiresTheBearerToken_WithTheDocumentedProblem()
    {
        using var anonymous = factory.CreateClient();

        foreach (var (method, path) in new[]
                 {
                     ("GET", $"/rpc/changesets/{UnknownId('a')}"),
                     ("GET", "/rpc/log"),
                     ("GET", "/rpc/path-history"),
                     ("GET", "/rpc/diff"),
                     ("GET", "/rpc/blame"),
                     ("GET", "/rpc/merge-base"),
                     ("POST", $"/rpc/blobs/{UnknownId('a')}"),
                     ("POST", "/rpc/changesets"),
                     ("POST", "/rpc/trees/missing"),
                     ("POST", "/rpc/blobs/missing"),
                     ("POST", "/rpc/merge"),
                     ("POST", "/rpc/rebase"),
                 })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "POST" ? new StringContent("{}", Encoding.UTF8, "application/json") : null,
            };
            var response = await anonymous.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
            var body = await response.JsonAsync();
            Assert.Equal("unauthorized", body.Code());
        }
    }

    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Seeds ───────────────────────────────────────────────────────────────────────────────────

    private async Task<string> CommitRootAsync()
    {
        // The root adds BOTH files the diverged-branch seed later modifies: docs/readme.md and
        // docs/shared.md ("shared: base"). A Modified entry assumes the path exists in the first
        // parent's snapshot — the object model rejects a modified-for-absent-path delta, so the
        // base commit is what makes the shared-file edits well-formed.
        var changeset = NewChangeset(
            "initial commit",
            [
                new ChangeEntry("docs/readme.md", null, Blob.Create(("docs: the beginning\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Added),
                new ChangeEntry("docs/shared.md", null, Blob.Create(("shared: base\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Added),
            ],
            []);
        var response = await PostChangesetAsync(
            changeset,
            refMove: new CreateChangesetRequest.RefMoveRequest("main", RefStore.CreationRevision),
            contents: ["docs: the beginning\n"u8.ToArray(), "shared: base\n"u8.ToArray()]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset.Id;
    }

    private async Task<string> CommitChildAsync(string root)
    {
        // The child APPENDS a second line rather than replacing the first, so blame at the tip
        // attributes line 1 to the root and line 2 to the child — the two-line family.
        var changeset = NewChangeset(
            "extend readme",
            [new ChangeEntry("docs/readme.md", Blob.Create(("docs: the beginning\n"u8).ToArray()).Hash, Blob.Create(("docs: the beginning\ndocs: the continuation\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Modified)],
            [root]);
        var response = await PostChangesetAsync(
            changeset,
            refMove: new CreateChangesetRequest.RefMoveRequest("main", 1),
            contents: ["docs: the beginning\ndocs: the continuation\n"u8.ToArray()]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset.Id;
    }

    private async Task<(string root, string tip)> SeedTwoCommitLineageAsync()
    {
        var root = await CommitRootAsync();
        var tip = await CommitChildAsync(root);
        return (root, tip);
    }

    /// <summary>
    /// Diverged history for the merge family: main advanced a shared-file change; the branch added
    /// a file (clean) then changed the same shared file from the same base (conflict).
    /// </summary>
    private async Task<(string root, string mainTip, string branchTip, string branchAdditive)> SeedDivergedBranchesAsync()
    {
        var root = await CommitRootAsync(); // main@1

        var additive = NewChangeset(
            "add agent",
            [new ChangeEntry("src/agent.cs", null, Blob.Create(("var agent = new Agent();\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Added)],
            [root]);
        var additiveResponse = await PostChangesetAsync(
            additive,
            refMove: new CreateChangesetRequest.RefMoveRequest("feature", RefStore.CreationRevision),
            contents: ["var agent = new Agent();\n"u8.ToArray()]);
        Assert.Equal(HttpStatusCode.Created, additiveResponse.StatusCode);

        var branchTip = NewChangeset(
            "branch edits shared",
            [new ChangeEntry("docs/shared.md", Blob.Create(("shared: base\n"u8).ToArray()).Hash, Blob.Create(("shared: branch side\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Modified)],
            [additive.Id]);
        // The shared file's pre-edit content is stored by the branch's base commit — the server
        // validates every referenced blob (old and new) exists before appending. The root commit
        // already uploaded it, so the idempotent re-upload answers OK rather than Created; both
        // mean the content is in the store.
        var baseBlob = await repo.UploadAsync("shared: base\n"u8.ToArray());
        Assert.True(baseBlob.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"blob upload answered {(int)baseBlob.StatusCode}");
        var branchTipResponse = await PostChangesetAsync(
            branchTip,
            refMove: new CreateChangesetRequest.RefMoveRequest("feature", 1),
            contents: ["shared: branch side\n"u8.ToArray()]);
        Assert.Equal(HttpStatusCode.Created, branchTipResponse.StatusCode);

        var mainTip = NewChangeset(
            "main edits shared",
            [new ChangeEntry("docs/shared.md", Blob.Create(("shared: base\n"u8).ToArray()).Hash, Blob.Create(("shared: main side\n"u8).ToArray()).Hash, FileContentKind.Regular, ChangeType.Modified)],
            [root]);
        var mainTipResponse = await PostChangesetAsync(
            mainTip,
            refMove: new CreateChangesetRequest.RefMoveRequest("main", 1),
            contents: ["shared: main side\n"u8.ToArray()]);
        Assert.Equal(HttpStatusCode.Created, mainTipResponse.StatusCode);

        return (root, mainTip.Id, branchTip.Id, additive.Id);
    }

    private async Task<string> CommitOrphanRootAsync()
    {
        var content = "an unrelated island of history"u8.ToArray();
        var changeset = NewChangeset(
            "orphan",
            [new ChangeEntry("other/orphan.txt", null, Blob.Create(content).Hash, FileContentKind.Regular, ChangeType.Added)],
            []);
        var response = await PostChangesetAsync(changeset, contents: [content]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return changeset.Id;
    }

    // ── Request helpers ─────────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostBlobAsync(byte[] content)
    {
        return await client.PostAsync($"/rpc/blobs/{Sha256.HashHex(content)}", new ByteArrayContent(content));
    }

    private async Task<HttpResponseMessage> PostChangesetAsync(
        Changeset changeset,
        string? idOverride = null,
        IReadOnlyList<byte[]>? contents = null,
        CreateChangesetRequest.RefMoveRequest? refMove = null)
    {
        var payload = new
        {
            id = idOverride ?? changeset.Id,
            parents = changeset.Parents.ToArray(),
            changes = changeset.Changes.ToArray(),
            message = changeset.Message,
            author = new { name = changeset.Author.Name, email = changeset.Author.Email },
            timestampUtc = changeset.TimestampUtc,
            blobs = contents?.Select(content => new
            {
                hash = Sha256.HashHex(content),
                content,
            }).ToArray() ?? Array.Empty<object>(),
            refMove,
        };
        return await repo.PostJsonAsync("/rpc/changesets", payload);
    }

    private async Task<HttpResponseMessage> MergeAsync(string ours, string theirs, string message)
    {
        return await repo.PostJsonAsync("/rpc/merge", new
        {
            ours,
            theirs,
            message,
            author = new { name = TestAuthor.Name, email = TestAuthor.Email },
            timestampUtc = 1_700_000_500,
        });
    }

    private async Task<HttpResponseMessage> RebaseAsync(string start, string tip, string newBase)
    {
        return await repo.PostJsonAsync("/rpc/rebase", new
        {
            start,
            tip,
            newBase,
        });
    }

    private async Task<HttpResponseMessage> PostTreesMissingAsync(object request)
    {
        return await repo.PostJsonAsync("/rpc/trees/missing", request);
    }

    private async Task<HttpResponseMessage> PostBlobsMissingAsync(IReadOnlyList<string> hashes)
    {
        return await repo.PostJsonAsync("/rpc/blobs/missing", new { blobs = hashes });
    }

    /// <summary>Builds a changeset with the quickstart's author and timestamp the way the client does.</summary>
    private static Changeset NewChangeset(string message, ChangeEntry[] changes, string[] parents, long timestampUtc = 1_700_000_000)
    {
        return Changeset.Create([.. parents], [.. changes], message, TestAuthor, timestampUtc);
    }

    private static string UnknownId(char seed) => new(seed, 64);

    private static async Task AssertMalformedBodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonAsync();
        Assert.Equal(MalformedBodyExceptionHandler.ErrorCode, body.Code());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
    }
}
