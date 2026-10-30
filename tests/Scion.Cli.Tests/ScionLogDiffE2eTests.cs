using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// <c>scn log</c> and <c>scn diff</c> end to end: the pages and deltas the CLI prints verified
/// against the server's own answers — log paging follows first-parent lineage newest-first with
/// the resume cursor, and a change renders the same lines locally (working copy) as remotely
/// (server-computed delta between the committed trees).
/// </summary>
[Collection("ScionCliSequential")]
public sealed class ScionLogDiffE2eTests : IDisposable
{
    private readonly CliHarness harness = new();

    [Fact]
    public async Task Log_PagesFirstParentHistoryNewestFirst_WithTheServerCursor()
    {
        Init();
        var ids = CommitSeries(5);

        // Page 1: the two newest, with the cursor that resumes the walk.
        var (page1Exit, page1) = harness.RunCli("log", "--limit", "2", "--oneline");
        Assert.Equal(0, page1Exit);
        Assert.Equal($"{ids[4]} five\n{ids[3]} four", PageLines(page1, out var page1Cursor));
        Assert.Equal(ids[3], page1Cursor);

        // Page 2 resumes after the cursor; the cursor advances again.
        var (page2Exit, page2) = harness.RunCli("log", "--limit", "2", "--after", page1Cursor!, "--oneline");
        Assert.Equal(0, page2Exit);
        Assert.Equal($"{ids[2]} three\n{ids[1]} two", PageLines(page2, out var page2Cursor));
        Assert.Equal(ids[1], page2Cursor);

        // The final page: the root commit, no cursor — the walk is over.
        var (page3Exit, page3) = harness.RunCli("log", "--limit", "2", "--after", page2Cursor!, "--oneline");
        Assert.Equal(0, page3Exit);
        Assert.Equal($"{ids[0]} one", PageLines(page3, out var page3Cursor));
        Assert.Null(page3Cursor);

        // The CLI's pages are the server's pages: same ids, same order.
        var serverLog = await harness.GetJsonAsync("/rpc/log?limit=5");
        var serverIds = serverLog.GetProperty("changesets").EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetString()).ToArray();
        var idsNewestFirst = ids.AsEnumerable().Reverse().ToArray();
        Assert.Equal(idsNewestFirst, serverIds);
    }

    [Fact]
    public async Task Log_FullFormat_RendersAuthorDateParentsAndMessage()
    {
        Init();
        File.WriteAllText(Path.Combine(harness.RepoRoot, "file.txt"), "v1");
        var (firstExit, firstOut) = harness.RunCli("commit", "-m", "root");
        Assert.Equal(0, firstExit);
        var firstId = CommittedId(firstOut);
        File.WriteAllText(Path.Combine(harness.RepoRoot, "file.txt"), "v2");
        var (_, secondOut) = harness.RunCli("commit", "-m", "second");

        var (exit, output) = harness.RunCli("log", "--limit", "1");
        Assert.Equal(0, exit);
        Assert.Contains($"commit {CommittedId(secondOut)}", output);
        Assert.Contains($"parent {firstId}", output);
        Assert.Contains("Author: Agent <agent@example.com>", output);
        Assert.Contains("Date: ", output);
        Assert.Contains("    second", output);

        // The root's record carries no parent line — it has no parents.
        var (rootExit, rootOutput) = harness.RunCli("log", "--limit", "1", "--tip", firstId);
        Assert.Equal(0, rootExit);
        Assert.DoesNotContain("parent", rootOutput);
        Assert.Contains("    root", rootOutput);
    }

    [Fact]
    public async Task Log_EmptyRepository_SaysSo()
    {
        Init();
        var (exit, output) = harness.RunCli("log");
        Assert.Equal(0, exit);
        Assert.Contains("no commits", output);
    }

    [Fact]
    public async Task Log_OutsideARepository_Fails()
    {
        var (exit, output) = harness.RunCli("log");
        Assert.Equal(1, exit);
        Assert.Contains("not a scion repository", output);
    }

    [Fact]
    public async Task Log_RejectsAnOutOfRangeLimit()
    {
        Init();
        var (exit, output) = harness.RunCli("log", "--limit", "0");
        Assert.Equal(1, exit);
        Assert.Contains("--limit", output);
    }

    [Fact]
    public async Task Diff_LocalAndRemoteRenderTheSameChangeLines()
    {
        Init();
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "readme");
        Directory.CreateDirectory(Path.Combine(harness.RepoRoot, "src"));
        File.WriteAllText(Path.Combine(harness.RepoRoot, "src", "hello.txt"), "hello scion");
        var (_, commitOut) = harness.RunCli("commit", "-m", "base");
        var baseId = CommittedId(commitOut);

        // A clean working copy is an empty diff — locally and against itself remotely.
        var (cleanExit, cleanOut) = harness.RunCli("diff");
        Assert.Equal(0, cleanExit);
        Assert.Contains("no changes", cleanOut);
        var (selfExit, selfOut) = harness.RunCli("diff", baseId, baseId);
        Assert.Equal(0, selfExit);
        Assert.Contains("no changes", selfOut);

        // One modify, one delete, one add.
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "readme v2");
        File.Delete(Path.Combine(harness.RepoRoot, "src", "hello.txt"));
        File.WriteAllText(Path.Combine(harness.RepoRoot, "src", "third.txt"), "third");

        var (localExit, localOut) = harness.RunCli("diff");
        Assert.Equal(0, localExit);
        Assert.Contains("M README.md", localOut);
        Assert.Contains("D src/hello.txt", localOut);
        Assert.Contains("A src/third.txt", localOut);

        // Commit, then ask the server for the same delta between the two trees.
        var (_, secondOut) = harness.RunCli("commit", "-m", "second");
        var secondId = CommittedId(secondOut);

        var (remoteExit, remoteOut) = harness.RunCli("diff", baseId, secondId);
        Assert.Equal(0, remoteExit);
        Assert.Contains("M README.md", remoteOut);
        Assert.Contains("D src/hello.txt", remoteOut);
        Assert.Contains("A src/third.txt", remoteOut);

        // The remote answer IS the server's answer, not a local re-computation.
        var serverDiff = await harness.GetJsonAsync($"/rpc/diff?from={baseId}&to={secondId}");
        var serverLines = serverDiff.GetProperty("changes").EnumerateArray()
            .Select(change => $"{StatusLetter(change.GetProperty("type").GetString()!)} {change.GetProperty("path").GetString()}")
            .ToHashSet();
        Assert.Contains("M README.md", serverLines);
        Assert.Contains("D src/hello.txt", serverLines);
        Assert.Contains("A src/third.txt", serverLines);
    }

    [Fact]
    public async Task Diff_WithOneChangeset_FailsWithUsage()
    {
        Init();
        var (exit, output) = harness.RunCli("diff", "only-one-side");
        Assert.Equal(1, exit);
        Assert.Contains("exactly two changesets", output);
    }

    [Fact]
    public async Task Diff_UnknownChangeset_FailsCleanly()
    {
        Init();
        var (exit, output) = harness.RunCli("diff", new string('a', 64), new string('b', 64));
        Assert.Equal(1, exit);
        Assert.Contains("404", output);
    }

    private void Init()
    {
        var (exit, output) = harness.RunCli(
            "init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken,
            "--name", "Agent", "--email", "agent@example.com");
        Assert.Equal(0, exit);
        Assert.Contains("Initialized an empty scion repository", output);
    }

    /// <summary>Commits <paramref name="count"/> changesets named "one"…"five", each touching one new file; returns the ids oldest first.</summary>
    private List<string> CommitSeries(int count)
    {
        List<string> ids = [];
        for (var i = 1; i <= count; i++)
        {
            File.WriteAllText(Path.Combine(harness.RepoRoot, $"file-{i}.txt"), $"content {i}");
            var (_, output) = harness.RunCli("commit", "-m", NumberWord(i));
            ids.Add(CommittedId(output));
        }

        return ids;
    }

    private static string NumberWord(int n) => n switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        _ => $"commit {n}",
    };

    private static string StatusLetter(string type) => type switch
    {
        "added" => "A",
        "modified" => "M",
        "deleted" => "D",
        "renamed" => "R",
        _ => "?",
    };

    /// <summary>Splits one-line log output into its entry lines; the cursor, if any, rides on the "next page:" line.</summary>
    private static string PageLines(string output, out string? cursor)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        cursor = lines.LastOrDefault(line => line.StartsWith("next page: scn log --after ", StringComparison.Ordinal)) is { } hint
            ? hint["next page: scn log --after ".Length..]
            : null;
        return string.Join("\n", lines.Where(line => !line.StartsWith("next page:", StringComparison.Ordinal)));
    }

    private static string CommittedId(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"committed ([0-9a-f]{64})");
        Assert.True(match.Success, $"Expected a committed changeset id in the output, got: {output}");
        return match.Groups[1].Value;
    }

    public void Dispose() => harness.Dispose();
}
