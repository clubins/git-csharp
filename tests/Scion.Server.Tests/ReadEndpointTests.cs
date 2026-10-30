using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scion.Core;
using Scion.Server.Endpoints;

namespace Scion.Server.Tests;

/// <summary>
/// End-to-end behavior of the read endpoints through the real pipeline: a stored changeset
/// fetched by id, the log paged along first-parent lineage from a tip with chained cursors, and
/// a path's touching changesets answered from the path-history derived index — plus the error
/// contract: unknown ids are 404 (spec C7), malformed query parameters are 400.
/// </summary>
public sealed class ReadEndpointTests : IDisposable
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

    public ReadEndpointTests()
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
    public async Task GetChangeset_ReturnsTheStoredRecordFieldForField()
    {
        var root = await CommitAsync(null, "initial commit", 1_700_000_000, Added("docs/readme.md", "docs: the beginning"));

        var response = await client.GetAsync($"/rpc/changesets/{root.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonBodyAsync(response);
        Assert.Equal(root.Id, body.GetProperty("id").GetString());
        Assert.Empty(body.GetProperty("parents").EnumerateArray());

        var change = body.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal("docs/readme.md", change.GetProperty("path").GetString());
        Assert.Equal("added", change.GetProperty("type").GetString());
        Assert.Equal("regular", change.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("oldBlob").ValueKind);
        Assert.Equal(root.Changes[0].NewBlob, change.GetProperty("newBlob").GetString());
        Assert.Equal("initial commit", body.GetProperty("message").GetString());
        Assert.Equal("Agent", body.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("agent@scion.test", body.GetProperty("author").GetProperty("email").GetString());
        Assert.Equal(1_700_000_000, body.GetProperty("timestampUtc").GetInt64());
    }

    [Fact]
    public async Task GetChangeset_ForAChild_CarriesItsParentChain()
    {
        var root = await CommitAsync(null, "initial", 1_700_000_000, Added("docs/readme.md", "docs: the beginning"));
        var child = await CommitAsync(root.Id, "extend", 1_700_000_100, Modified("docs/readme.md", root.Changes[0].NewBlob!, "docs: more"));

        var response = await client.GetAsync($"/rpc/changesets/{child.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonBodyAsync(response);
        var parents = body.GetProperty("parents").EnumerateArray().Select(parent => parent.GetString()!).ToArray();
        string[] expectedParents = [root.Id];
        Assert.Equal(expectedParents, parents);
        Assert.Equal("modified", body.GetProperty("changes").EnumerateArray().Single().GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetChangeset_UnknownId_Is404WithTheErrorContract()
    {
        var response = await client.GetAsync($"/rpc/changesets/{UnknownId()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonBodyAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("changeset", problem.GetProperty("objectType").GetString());
        Assert.Contains("does not exist", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Log_PagesTheFirstParentLineageNewestFirst_WithChainedCursors()
    {
        var chain = await CommitChainAsync(length: 5);
        string[] expectedLineage = [chain[4].Id, chain[3].Id, chain[2].Id, chain[1].Id, chain[0].Id];

        var (firstPage, firstResponse) = await GetLogAsync(tip: chain[4].Id, after: null, limit: "2");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(expectedLineage[..2], IdsOfPage(firstPage));
        Assert.Equal(chain[3].Id, firstPage.GetProperty("nextAfter").GetString());

        var (secondPage, _) = await GetLogAsync(tip: null, after: chain[3].Id, limit: "2");
        Assert.Equal(expectedLineage[2..4], IdsOfPage(secondPage));
        Assert.Equal(chain[1].Id, secondPage.GetProperty("nextAfter").GetString());

        var (thirdPage, _) = await GetLogAsync(tip: null, after: chain[1].Id, limit: "2");
        Assert.Equal([chain[0].Id], IdsOfPage(thirdPage));
        Assert.Equal(JsonValueKind.Null, thirdPage.GetProperty("nextAfter").ValueKind);

        // The cursor at the root has nothing left to offer: an empty page, again without a cursor.
        var (fourthPage, _) = await GetLogAsync(tip: null, after: chain[0].Id, limit: "2");
        Assert.Empty(IdsOfPage(fourthPage));
        Assert.Equal(JsonValueKind.Null, fourthPage.GetProperty("nextAfter").ValueKind);
    }

    [Fact]
    public async Task Log_WithoutATip_StartsAtTheNewestAppendedChangeset()
    {
        var chain = await CommitChainAsync(length: 3);

        var (page, response) = await GetLogAsync(tip: null, after: null, limit: "2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] expectedHead = [chain[2].Id, chain[1].Id];
        Assert.Equal(expectedHead, IdsOfPage(page));
        Assert.Equal(chain[1].Id, page.GetProperty("nextAfter").GetString());

        // The cursor pages on without any tip: the walk resumes from the cursor's first parent.
        var (remainder, _) = await GetLogAsync(tip: null, after: chain[1].Id, limit: "2");
        Assert.Equal([chain[0].Id], IdsOfPage(remainder));
    }

    [Fact]
    public async Task Log_OnAnEmptyLog_IsAnEmptyPageWithoutCursor()
    {
        var (page, response) = await GetLogAsync(tip: null, after: null, limit: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(IdsOfPage(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextAfter").ValueKind);
    }

    [Fact]
    public async Task Log_FromAMerge_WalksFirstParentOnly()
    {
        var root = await CommitAsync(null, "root", 1_700_000_000, Added("docs/readme.md", "v1"));
        var sideOne = await CommitAsync(root.Id, "side one", 1_700_000_100, Added("docs/one.md", "one"));
        var sideTwo = await CommitAsync(root.Id, "side two", 1_700_000_200, Added("docs/two.md", "two"));
        var merge = await CommitMergeAsync(sideOne.Id, sideTwo.Id, 1_700_000_300);

        var (page, response) = await GetLogAsync(tip: merge.Id, after: null, limit: "10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] expectedLineage = [merge.Id, sideOne.Id, root.Id];
        Assert.Equal(expectedLineage, IdsOfPage(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextAfter").ValueKind);
    }

    [Fact]
    public async Task Log_WithAnUnknownTip_Is404()
    {
        await CommitChainAsync(length: 1);

        var (_, response) = await GetLogAsync(tip: UnknownId(), after: null, limit: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonBodyAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("changeset", problem.GetProperty("objectType").GetString());
    }

    [Fact]
    public async Task Log_WithAnUnknownAfter_Is404()
    {
        var chain = await CommitChainAsync(length: 1);

        var (_, response) = await GetLogAsync(tip: chain[0].Id, after: UnknownId(), limit: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonBodyAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Log_RejectsLimitsOutsideThePageBound()
    {
        await CommitChainAsync(length: 1);

        foreach (var limit in new[] { "0", "-3", "abc", "1001" })
        {
            var (_, response) = await GetLogAsync(tip: null, after: null, limit: limit);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await JsonBodyAsync(response);
            Assert.Equal("validation", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task PathHistory_AnswersTheTouchingChangesetsOldestFirst()
    {
        var add = await CommitAsync(null, "add readme", 1_700_000_000, Added("docs/readme.md", "v1"));
        var firstEdit = await CommitAsync(add.Id, "edit readme", 1_700_000_100, Modified("docs/readme.md", add.Changes[0].NewBlob!, "v2"));
        var guide = await CommitAsync(firstEdit.Id, "add guide", 1_700_000_200, Added("docs/guide.md", "g1"));
        var secondEdit = await CommitAsync(guide.Id, "edit readme again", 1_700_000_300, Modified("docs/readme.md", firstEdit.Changes[0].NewBlob!, "v3"));

        var (body, response) = await GetPathHistoryAsync(secondEdit.Id, "docs/readme.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("docs/readme.md", body.GetProperty("path").GetString());
        string[] expectedChain = [add.Id, firstEdit.Id, secondEdit.Id];
        Assert.Equal(expectedChain, ChainOf(body));

        var (guideBody, _) = await GetPathHistoryAsync(secondEdit.Id, "docs/guide.md");
        Assert.Equal([guide.Id], ChainOf(guideBody));
    }

    [Fact]
    public async Task PathHistory_ForAPathNeverTouched_IsAnEmptyChain()
    {
        var chain = await CommitChainAsync(length: 2);

        var (body, response) = await GetPathHistoryAsync(chain[1].Id, "docs/never-touched.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("docs/never-touched.md", body.GetProperty("path").GetString());
        Assert.Empty(ChainOf(body));
    }

    [Fact]
    public async Task PathHistory_WithAnUnknownChangeset_Is404()
    {
        await CommitChainAsync(length: 1);

        var (_, response) = await GetPathHistoryAsync(UnknownId(), "docs/readme.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await JsonBodyAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("changeset", problem.GetProperty("objectType").GetString());
    }

    [Fact]
    public async Task PathHistory_WithoutRequiredParams_Is400()
    {
        var chain = await CommitChainAsync(length: 1);

        var (_, noParams) = await GetPathHistoryAsync(changeset: null, path: "docs/readme.md");
        Assert.Equal(HttpStatusCode.BadRequest, noParams.StatusCode);

        var (_, noPath) = await GetPathHistoryAsync(chain[0].Id, path: null);
        Assert.Equal(HttpStatusCode.BadRequest, noPath.StatusCode);
    }

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

    /// <summary>Builds a linear first-parent chain, each changeset modifying docs/readme.md once more.</summary>
    private async Task<List<Changeset>> CommitChainAsync(int length)
    {
        List<Changeset> chain = [];
        for (var i = 0; i < length; i++)
        {
            var message = $"commit {i + 1}";
            var changes = chain.Count == 0
                ? new[] { Added("docs/readme.md", $"content {i + 1}") }
                : new[] { Modified("docs/readme.md", chain[^1].Changes[0].NewBlob!, $"content {i + 1}") };
            chain.Add(await CommitAsync(chain.Count == 0 ? null : chain[^1].Id, message, 1_700_000_000 + i, changes));
        }

        return chain;
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

    private async Task<(JsonElement Body, HttpResponseMessage Response)> GetLogAsync(string? tip, string? after, string? limit)
    {
        List<string> query = [];
        if (tip is not null)
        {
            query.Add($"tip={tip}");
        }

        if (after is not null)
        {
            query.Add($"after={after}");
        }

        if (limit is not null)
        {
            query.Add($"limit={limit}");
        }

        var separator = query.Count == 0 ? string.Empty : "?";
        var response = await client.GetAsync($"/rpc/log{separator}{string.Join('&', query)}");
        return (await JsonBodyAsync(response), response);
    }

    private async Task<(JsonElement Body, HttpResponseMessage Response)> GetPathHistoryAsync(string? changeset, string? path)
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
        var response = await client.GetAsync($"/rpc/path-history{separator}{string.Join('&', query)}");
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

    /// <summary>The ids of a log page's changesets, in wire order (newest first).</summary>
    private static string[] IdsOfPage(JsonElement page)
    {
        return page.GetProperty("changesets").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!).ToArray();
    }

    /// <summary>The changeset ids of a path-history body's chain, in wire order (oldest first).</summary>
    private static string[] ChainOf(JsonElement body)
    {
        return body.GetProperty("changesets").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
    }

    private static string UnknownId()
    {
        return new string('a', 64);
    }
}
