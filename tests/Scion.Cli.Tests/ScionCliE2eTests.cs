using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scion.Cli.Local;
using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// The thin-client loop end to end: init against the test server, edit, commit, status — the
/// changeset verified server-side (id, parents, changes, author) through the shared factory's
/// authenticated client, and the local state advanced by the commit itself.
/// </summary>
[Collection("ScionCliSequential")]
public sealed class ScionCliE2eTests : IDisposable
{
    private readonly CliHarness harness = new();

    [Fact]
    public async Task Init_Edit_Commit_RoundTripsThroughTheServer()
    {
        var (initExit, initOut) = harness.RunCli(
            "init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken,
            "--name", "Agent", "--email", "agent@example.com");
        Assert.Equal(0, initExit);
        Assert.Contains("Initialized an empty scion repository", initOut);

        // An empty repository: no tip yet, nothing to report.
        var (emptyStatusExit, emptyStatus) = harness.RunCli("status");
        Assert.Equal(0, emptyStatusExit);
        Assert.Contains("tip: (none)", emptyStatus);
        Assert.Contains("no changes", emptyStatus);

        // Edit: two files land on disk, one nested.
        Directory.CreateDirectory(Path.Combine(harness.RepoRoot, "src"));
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "readme");
        File.WriteAllText(Path.Combine(harness.RepoRoot, "src", "hello.txt"), "hello scion");

        var (statusExit, statusOut) = harness.RunCli("status");
        Assert.Equal(0, statusExit);
        Assert.Contains("A README.md", statusOut);
        Assert.Contains("A src/hello.txt", statusOut);
        Assert.Contains("2 change(s): 2 added, 0 modified, 0 deleted", statusOut);

        var (commitExit, commitOut) = harness.RunCli("commit", "-m", "first");
        Assert.Equal(0, commitExit);
        var firstId = CommittedId(commitOut);

        // The server holds exactly what the client shipped.
        var stored = await harness.GetJsonAsync($"/rpc/changesets/{firstId}");
        Assert.Equal("first", stored.GetProperty("message").GetString());
        Assert.Equal("Agent", stored.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("agent@example.com", stored.GetProperty("author").GetProperty("email").GetString());
        Assert.Empty(stored.GetProperty("parents").EnumerateArray());

        var expectedReadmeHash = HashOf("readme");
        var expectedHelloHash = HashOf("hello scion");
        var storedChanges = stored.GetProperty("changes");
        Assert.Equal(2, storedChanges.GetArrayLength());
        Assert.Equal("README.md", storedChanges[0].GetProperty("path").GetString());
        Assert.Equal("added", storedChanges[0].GetProperty("type").GetString());
        Assert.Equal("regular", storedChanges[0].GetProperty("kind").GetString());
        Assert.Equal(expectedReadmeHash, storedChanges[0].GetProperty("newBlob").GetString());
        Assert.Equal("src/hello.txt", storedChanges[1].GetProperty("path").GetString());
        Assert.Equal(expectedHelloHash, storedChanges[1].GetProperty("newBlob").GetString());
        Assert.Equal(JsonValueKind.Null, storedChanges[1].GetProperty("oldBlob").ValueKind);

        // The commit advanced the local tip, so status is clean and names the new tip.
        Assert.Equal(firstId, WorkingState.Load(harness.RepoRoot).Tip);
        var (cleanExit, cleanOut) = harness.RunCli("status");
        Assert.Equal(0, cleanExit);
        Assert.Contains($"tip: {firstId}", cleanOut);
        Assert.Contains("no changes", cleanOut);

        // Second round: one modify, one delete, one add — a full Add/Modify/Delete changeset.
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "readme v2");
        File.Delete(Path.Combine(harness.RepoRoot, "src", "hello.txt"));
        File.WriteAllText(Path.Combine(harness.RepoRoot, "src", "third.txt"), "third");

        var (status2Exit, status2Out) = harness.RunCli("status");
        Assert.Equal(0, status2Exit);
        Assert.Contains("M README.md", status2Out);
        Assert.Contains("D src/hello.txt", status2Out);
        Assert.Contains("A src/third.txt", status2Out);
        Assert.Contains("3 change(s): 1 added, 1 modified, 1 deleted", status2Out);

        var (commit2Exit, commit2Out) = harness.RunCli("commit", "-m", "second");
        Assert.Equal(0, commit2Exit);
        var secondId = CommittedId(commit2Out);

        var second = await harness.GetJsonAsync($"/rpc/changesets/{secondId}");
        var parents = second.GetProperty("parents");
        Assert.Equal(1, parents.GetArrayLength());
        Assert.Equal(firstId, parents[0].GetString());

        var secondChanges = second.GetProperty("changes");
        Assert.Equal(3, secondChanges.GetArrayLength());
        Assert.Equal("modified", secondChanges[0].GetProperty("type").GetString());
        Assert.Equal(expectedReadmeHash, secondChanges[0].GetProperty("oldBlob").GetString());
        Assert.Equal(HashOf("readme v2"), secondChanges[0].GetProperty("newBlob").GetString());
        Assert.Equal("deleted", secondChanges[1].GetProperty("type").GetString());
        Assert.Equal(expectedHelloHash, secondChanges[1].GetProperty("oldBlob").GetString());
        Assert.Equal(JsonValueKind.Null, secondChanges[1].GetProperty("newBlob").ValueKind);
        Assert.Equal("added", secondChanges[2].GetProperty("type").GetString());

        // The log pages newest-first along first-parent lineage: both commits, second first.
        var log = await harness.GetJsonAsync("/rpc/log");
        var logIds = log.GetProperty("changesets").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { secondId, firstId }, logIds);
        Assert.Equal(JsonValueKind.Null, log.GetProperty("nextAfter").ValueKind);
    }

    [Fact]
    public void Init_RefusesToReinitializeAnExistingRepository()
    {
        Assert.Equal(0, harness.RunCli("init", "--server", harness.ServerUrl).Exit);

        var (reExit, reOut) = harness.RunCli("init", "--server", harness.ServerUrl);
        Assert.Equal(1, reExit);
        Assert.Contains("already a scion repository", reOut);
    }

    [Fact]
    public void Status_OutsideARepository_Fails()
    {
        var (exit, output) = harness.RunCli("status");
        Assert.Equal(1, exit);
        Assert.Contains("not a scion repository", output);
    }

    [Fact]
    public async Task Commit_WithoutAuthor_FailsWithGuidance()
    {
        Assert.Equal(0, harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken).Exit);
        File.WriteAllText(Path.Combine(harness.RepoRoot, "file.txt"), "content");

        var (exit, output) = harness.RunCli("commit", "-m", "no author");
        Assert.Equal(1, exit);
        Assert.Contains("No author configured", output);

        // With an author the very same commit succeeds — and the server confirms the author rode along.
        var (retryExit, retryOut) = harness.RunCli("commit", "-m", "with author", "--name", "Agent", "--email", "agent@example.com");
        Assert.Equal(0, retryExit);
        var id = CommittedId(retryOut);
        var stored = await harness.GetJsonAsync($"/rpc/changesets/{id}");
        Assert.Equal(id, stored.GetProperty("id").GetString());
        Assert.Equal("Agent", stored.GetProperty("author").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Commit_WithUnknownTip_FailsCleanly()
    {
        // A tip the server does not hold (a stale working copy against a reset server) surfaces
        // the server's own 404 — the server is authoritative; the client never guesses.
        Assert.Equal(0, harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken, "--name", "A", "--email", "a@b.c").Exit);
        File.WriteAllText(Path.Combine(harness.RepoRoot, "file.txt"), "content");
        Assert.Equal(0, harness.RunCli("commit", "-m", "root").Exit);

        var statePath = Path.Combine(harness.RepoRoot, ".scn", "state.json");
        var sabotaged = JsonSerializer.Serialize(
            new FakeState(new string('a', 64), WorkingState.Load(harness.RepoRoot).Files),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(statePath, sabotaged);

        File.WriteAllText(Path.Combine(harness.RepoRoot, "file.txt"), "content v2");
        var (exit, output) = harness.RunCli("commit", "-m", "orphan");
        Assert.Equal(1, exit);
        Assert.Contains("404", output);
    }

    private sealed record FakeState(string? Tip, Dictionary<string, WorkingState.Entry> Files);

    private static string CommittedId(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"committed ([0-9a-f]{64})");
        Assert.True(match.Success, $"Expected a committed changeset id in the output, got: {output}");
        return match.Groups[1].Value;
    }

    private static string HashOf(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public void Dispose() => harness.Dispose();
}
