using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scion.Cli.Local;
using Scion.Core;
using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// The materializer end to end through the real pipeline: a committed tree materialized back
/// (the restore round-trip), kinds restored (executable bit, symlink), the cold start — a fresh
/// init filling itself from the server's newest changeset — a first read of exactly 100 paths out
/// of a 10,000-path repository, and the clean failures when there is nothing or the server does
/// not hold what was named.
/// </summary>
[Collection("ScionCliSequential")]
public sealed class MaterializerE2eTests : IDisposable
{
    private readonly CliHarness harness = new();

    [Fact]
    public async Task Checkout_AfterLocalDrift_RestoresTheCommittedTree()
    {
        Init();
        Directory.CreateDirectory(Path.Combine(harness.RepoRoot, "src"));
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "readme");
        File.WriteAllText(Path.Combine(harness.RepoRoot, "src", "hello.txt"), "hello scion");
        var (commitExit, commitOut) = harness.RunCli("commit", "-m", "first", "--name", "A", "--email", "a@b.c");
        Assert.Equal(0, commitExit);
        var id = CommittedId(commitOut);

        // Local drift: one modified, one deleted — checkout is the restore.
        File.WriteAllText(Path.Combine(harness.RepoRoot, "README.md"), "drifted");
        File.Delete(Path.Combine(harness.RepoRoot, "src", "hello.txt"));

        var (exit, output) = harness.RunCli("checkout");
        AssertExit(exit, output);
        Assert.Contains($"checked out {id}", output);
        Assert.Equal("readme", File.ReadAllText(Path.Combine(harness.RepoRoot, "README.md")));
        Assert.Equal("hello scion", File.ReadAllText(Path.Combine(harness.RepoRoot, "src", "hello.txt")));

        // The manifest IS the materialized state: status answers clean, entirely locally.
        var (statusExit, statusOut) = harness.RunCli("status");
        Assert.Equal(0, statusExit);
        Assert.Contains($"tip: {id}", statusOut);
        Assert.Contains("no changes", statusOut);
    }

    [Fact]
    public void Checkout_RestoresExecutableAndSymlinkKinds()
    {
        // The executable bit and symlinks are POSIX semantics; the scanner and the materializer
        // agree on them where they exist (CI runs Linux).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Init();
        var script = Path.Combine(harness.RepoRoot, "run.sh");
        File.WriteAllText(script, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(harness.RepoRoot, "latest"), "run.sh");

        Assert.Equal(0, harness.RunCli("commit", "-m", "kinds", "--name", "A", "--email", "a@b.c").Exit);

        File.Delete(script);
        File.Delete(Path.Combine(harness.RepoRoot, "latest"));

        Assert.Equal(0, harness.RunCli("checkout").Exit);

        Assert.True(
            File.GetUnixFileMode(script).HasFlag(UnixFileMode.UserExecute),
            "the executable bit should be restored");
        Assert.Equal("run.sh", new FileInfo(Path.Combine(harness.RepoRoot, "latest")).LinkTarget);

        var (statusExit, statusOut) = harness.RunCli("status");
        Assert.Equal(0, statusExit);
        Assert.Contains("no changes", statusOut);
    }

    [Fact]
    public async Task ColdStart_InitThenCheckout_MaterializesTheServersNewestTree()
    {
        // One server, two working copies: the source commits 100 files; the second working copy
        // — freshly initialized, no tip, no files — checks out from that same server.
        using var source = new TempDirectory();
        var (sourceInit, sourceInitOut) = harness.RunIn(source.Path, "init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken, "--name", "A", "--email", "a@b.c");
        AssertExit(sourceInit, sourceInitOut);
        Directory.CreateDirectory(Path.Combine(source.Path, "data"));
        for (var i = 0; i < 100; i++)
        {
            File.WriteAllText(Path.Combine(source.Path, "data", $"file_{i:D4}.txt"), $"content {i}");
        }

        var (commitExit, commitOut) = harness.RunIn(source.Path, "commit", "-m", "hundred files", "--name", "A", "--email", "a@b.c");
        AssertExit(commitExit, commitOut);
        var id = CommittedId(commitOut);

        Assert.Equal(0, harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken).Exit);
        var (checkoutExit, checkoutOut) = harness.RunCli("checkout");
        AssertExit(checkoutExit, checkoutOut);
        Assert.Contains($"checked out {id}", checkoutOut);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal($"content {i}", File.ReadAllText(Path.Combine(harness.RepoRoot, "data", $"file_{i:D4}.txt")));
        }

        // The manifest update: the tip is the committed changeset and the manifest holds exactly
        // the 100 materialized paths.
        var state = WorkingState.Load(harness.RepoRoot);
        Assert.Equal(id, state.Tip);
        Assert.Equal(100, state.Files.Count);

        var (statusExit, statusOut) = harness.RunCli("status");
        Assert.Equal(0, statusExit);
        Assert.Contains("no changes", statusOut);
    }

    [Fact]
    public async Task Checkout_RequestedPaths_MaterializesExactlyThose()
    {
        // A 10,000-path repository, seeded straight through the server API; the client's first
        // read names one glob that matches exactly 100 paths and materializes exactly those —
        // the other 9,900 never cross the wire and never land on disk.
        const int TotalFiles = 10_000;
        await SeedRepositoryAsync(TotalFiles);

        Assert.Equal(0, harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken).Exit);
        var (exit, output) = harness.RunCli("checkout", "--path", "data/file_00*.txt");
        AssertExit(exit, output);
        Assert.Contains("100 path(s)", output);

        Assert.Equal("content 0", File.ReadAllText(Path.Combine(harness.RepoRoot, "data", "file_0000.txt")));
        Assert.Equal("content 42", File.ReadAllText(Path.Combine(harness.RepoRoot, "data", "file_0042.txt")));
        Assert.Equal("content 99", File.ReadAllText(Path.Combine(harness.RepoRoot, "data", "file_0099.txt")));
        Assert.Equal(100, Directory.EnumerateFiles(Path.Combine(harness.RepoRoot, "data")).Count());
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "data", "file_0100.txt")));
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "data", "file_9999.txt")));

        // The manifest holds exactly the materialized state — the 100 requested paths.
        Assert.Equal(100, WorkingState.Load(harness.RepoRoot).Files.Count);
    }

    [Fact]
    public void Checkout_WithNoServerChangeset_FailsWithGuidance()
    {
        Init();

        var (exit, output) = harness.RunCli("checkout");

        Assert.Equal(1, exit);
        Assert.Contains("Nothing to check out", output);
    }

    [Fact]
    public void Checkout_WithUnknownChangeset_FailsCleanly()
    {
        Init();

        var (exit, output) = harness.RunCli("checkout", new string('a', 64));

        Assert.Equal(1, exit);
        Assert.Contains("404", output);
    }

    private void Init()
    {
        Assert.Equal(0, harness.RunCli(
            "init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken,
            "--name", "A", "--email", "a@b.c").Exit);
    }

    /// <summary>Seeds one root changeset of <paramref name="fileCount"/> small files through the server API — the 10k-path fixture.</summary>
    private async Task SeedRepositoryAsync(int fileCount)
    {
        var changes = new ChangeEntry[fileCount];
        var blobs = new object[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var blob = Blob.Create(Encoding.UTF8.GetBytes($"content {i}"));
            changes[i] = new ChangeEntry($"data/file_{i:D4}.txt", null, blob.Hash, FileContentKind.Regular, ChangeType.Added);
            blobs[i] = new { hash = blob.Hash, content = blob.Content.ToArray() };
        }

        var changeset = Changeset.Create([], [.. changes], "seed 10k", new Author("Seed", "seed@scion.test"), 1_790_000_000);
        var payload = JsonSerializer.Serialize(new
        {
            id = changeset.Id,
            parents = Array.Empty<string>(),
            changes = changeset.Changes.ToArray(),
            message = changeset.Message,
            author = new { name = changeset.Author.Name, email = changeset.Author.Email },
            timestampUtc = changeset.TimestampUtc,
            blobs,
        }, SeedJson);

        using var client = harness.Factory.CreateAuthedClient();
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/rpc/changesets", body);
        response.EnsureSuccessStatusCode();
    }

    private static readonly JsonSerializerOptions SeedJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void AssertExit(int exit, string output)
    {
        // The output rides in the failure message — an e2e failure without the CLI's own words is undiagnosable.
        Assert.True(exit == 0, $"scn exited {exit}:\n{output}");
    }

    private static string CommittedId(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"committed ([0-9a-f]{64})");
        Assert.True(match.Success, $"Expected a committed changeset id in the output, got: {output}");
        return match.Groups[1].Value;
    }

    public void Dispose() => harness.Dispose();
}
