using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scion.Cli.Local;
using Scion.Core;
using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// Sparse profiles end to end: <c>--sparse</c> materializes only its matches and persists the
/// profile, the profile is sticky on later checkouts (the server's sparse filter driving the
/// fetch), and the upgrade paths — a wider profile, then <c>--no-sparse</c> for the full tree —
/// each leave the manifest recording exactly the materialized state. The fixture: one seeded
/// changeset of 31 files across <c>src/</c>, <c>docs/</c>, <c>tests/</c>, and a root README.
/// </summary>
[Collection("ScionCliSequential")]
public sealed class SparseCheckoutE2eTests : IDisposable
{
    private readonly CliHarness harness = new();

    [Fact]
    public async Task Checkout_SparseProfile_MaterializesOnlyProfilePaths_AndPersists()
    {
        await SeedTreeAsync(parentId: null);

        // Cold start with a profile: fresh init, no tip, first read scoped to src/.
        Init();
        var (exit, output) = harness.RunCli("checkout", "--sparse", "src/**");
        AssertExit(exit, output);
        Assert.Contains("10 path(s)", output);
        Assert.Contains("sparse profile set: src/**", output);

        AssertSrcMaterialized();
        Assert.False(Directory.Exists(Path.Combine(harness.RepoRoot, "docs")));
        Assert.False(Directory.Exists(Path.Combine(harness.RepoRoot, "tests")));
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "README.md")));

        // Manifest exactness: exactly the 10 profile-matched paths, no more.
        var state = WorkingState.Load(harness.RepoRoot);
        Assert.Equal(10, state.Files.Count);
        Assert.All(
            Enumerable.Range(0, 10).Select(i => $"src/a_{i:D2}.txt"),
            path => Assert.True(state.Files.ContainsKey(path), $"the manifest is missing '{path}'"));

        // The profile persisted: the file is there and reads back as given.
        var profile = SparseProfileState.Load(harness.RepoRoot);
        Assert.NotNull(profile);
        Assert.Equal(["src/**"], profile.Patterns);

        AssertStatusClean();
    }

    [Fact]
    public async Task Checkout_WithPersistedProfile_StaysScopedOnLaterCheckouts()
    {
        var first = await SeedTreeAsync(parentId: null);
        // A second changeset: one more src file and one more docs file.
        var second = await SeedTreeAsync(
            first,
            ("src/a_10.txt", "src content 10"),
            ("docs/b_10.txt", "docs content 10"));

        Init();
        Assert.Equal(0, harness.RunCli("checkout", "--sparse", "src/**").Exit);

        // The sticky profile scopes a named-changeset checkout too — the later tree's docs
        // file is out of profile and never lands.
        var (exit, output) = harness.RunCli("checkout", second);
        AssertExit(exit, output);
        Assert.Contains("11 path(s)", output);
        Assert.Contains("sparse profile: src/**", output);

        Assert.Equal("src content 10", File.ReadAllText(Path.Combine(harness.RepoRoot, "src", "a_10.txt")));
        Assert.Equal(11, WorkingState.Load(harness.RepoRoot).Files.Count);
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "docs", "b_10.txt")));
        AssertStatusClean();
    }

    [Fact]
    public async Task Checkout_NoSparse_UpgradeToFullTree_AndClearsTheProfile()
    {
        await SeedTreeAsync(parentId: null);

        Init();
        Assert.Equal(0, harness.RunCli("checkout", "--sparse", "src/**").Exit);
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "README.md")));

        // The upgrade path: clear the profile, materialize the whole tree.
        var (exit, output) = harness.RunCli("checkout", "--no-sparse");
        AssertExit(exit, output);
        Assert.Contains("31 path(s)", output);
        Assert.Contains("sparse profile cleared", output);

        AssertSrcMaterialized();
        Assert.Equal("readme", File.ReadAllText(Path.Combine(harness.RepoRoot, "README.md")));
        Assert.Equal("docs content 3", File.ReadAllText(Path.Combine(harness.RepoRoot, "docs", "b_03.txt")));
        Assert.Equal("tests content 9", File.ReadAllText(Path.Combine(harness.RepoRoot, "tests", "t_09.txt")));
        Assert.Equal(31, WorkingState.Load(harness.RepoRoot).Files.Count);
        Assert.Null(SparseProfileState.Load(harness.RepoRoot));

        AssertStatusClean();
    }

    [Fact]
    public async Task Checkout_SparseWidened_MaterializesTheUnion_AndPersistsIt()
    {
        await SeedTreeAsync(parentId: null);

        Init();
        Assert.Equal(0, harness.RunCli("checkout", "--sparse", "src/**").Exit);

        // Widen the profile: existing scope stays, the new scope arrives, the manifest is the union.
        var (exit, output) = harness.RunCli("checkout", "--sparse", "src/**", "--sparse", "docs/**");
        AssertExit(exit, output);
        Assert.Contains("20 path(s)", output);

        AssertSrcMaterialized();
        Assert.Equal("docs content 7", File.ReadAllText(Path.Combine(harness.RepoRoot, "docs", "b_07.txt")));
        Assert.Equal(20, WorkingState.Load(harness.RepoRoot).Files.Count);
        Assert.Equal(["src/**", "docs/**"], SparseProfileState.Load(harness.RepoRoot)!.Patterns);
        Assert.False(File.Exists(Path.Combine(harness.RepoRoot, "tests", "t_00.txt")));

        AssertStatusClean();
    }

    [Fact]
    public async Task Checkout_SparseProfileMatchingNothing_MaterializesNothing()
    {
        await SeedTreeAsync(parentId: null);

        Init();
        var (exit, output) = harness.RunCli("checkout", "--sparse", "absent/**");
        AssertExit(exit, output);
        Assert.Contains("0 path(s)", output);

        var state = WorkingState.Load(harness.RepoRoot);
        Assert.Empty(state.Files);
        AssertStatusClean();
    }

    [Fact]
    public void Checkout_SparseAndNoSparseTogether_FailsAsContradictory()
    {
        Init();

        var (exit, output) = harness.RunCli("checkout", "--sparse", "src/**", "--no-sparse");

        Assert.Equal(1, exit);
        Assert.Contains("contradictory", output);
    }

    [Fact]
    public async Task Checkout_SparseWithBlankPattern_FailsAndPersistsNothing()
    {
        await SeedTreeAsync(parentId: null);

        Init();
        var (exit, output) = harness.RunCli("checkout", "--sparse", " ");
        Assert.Equal(1, exit);
        Assert.Contains("non-empty globs", output);

        // Nothing materialized, nothing persisted: a rejected profile never reaches disk.
        Assert.False(Directory.Exists(Path.Combine(harness.RepoRoot, "src")));
        Assert.Null(SparseProfileState.Load(harness.RepoRoot));
    }

    [Fact]
    public async Task Checkout_SparseRepeatedPatterns_PersistDeduplicated()
    {
        await SeedTreeAsync(parentId: null);

        Init();
        Assert.Equal(0, harness.RunCli("checkout", "--sparse", "src/**", "--sparse", "src/**").Exit);

        Assert.Equal(["src/**"], SparseProfileState.Load(harness.RepoRoot)!.Patterns);
        Assert.Equal(10, WorkingState.Load(harness.RepoRoot).Files.Count);
    }

    private void Init() =>
        Assert.Equal(0, harness.RunCli(
            "init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken,
            "--name", "A", "--email", "a@b.c").Exit);

    private void AssertStatusClean()
    {
        var (statusExit, statusOut) = harness.RunCli("status");
        Assert.True(statusExit == 0, $"scn status exited {statusExit}:\n{statusOut}");
        Assert.Contains("no changes", statusOut);
    }

    private void AssertSrcMaterialized()
    {
        Assert.Equal("src content 0", File.ReadAllText(Path.Combine(harness.RepoRoot, "src", "a_00.txt")));
        Assert.Equal("src content 9", File.ReadAllText(Path.Combine(harness.RepoRoot, "src", "a_09.txt")));
        Assert.Equal(10, Directory.EnumerateFiles(Path.Combine(harness.RepoRoot, "src")).Count());
    }

    /// <summary>
    /// Seeds one changeset through the server API. The root changeset's delta is the 31-file
    /// fixture; a child changeset's delta is only its extra files — a delta is relative to the
    /// first parent, and re-adding unchanged paths would fail the server's applicability check.
    /// </summary>
    private async Task<string> SeedTreeAsync(string? parentId, params (string Path, string Content)[] extraFiles)
    {
        var files = parentId is null ? TreeFixture() : extraFiles.ToList();

        var changes = new ChangeEntry[files.Count];
        var blobs = new object[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var blob = Blob.Create(Encoding.UTF8.GetBytes(files[i].Content));
            changes[i] = new ChangeEntry(files[i].Path, null, blob.Hash, FileContentKind.Regular, ChangeType.Added);
            blobs[i] = new { hash = blob.Hash, content = blob.Content.ToArray() };
        }

        var parents = parentId is null ? Array.Empty<string>() : new[] { parentId };
        var changeset = Changeset.Create(
            parentId is null ? [] : [parentId],
            [.. changes],
            "seed",
            new Author("Seed", "seed@scion.test"),
            1_790_000_000);
        var payload = JsonSerializer.Serialize(new
        {
            id = changeset.Id,
            parents,
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
        return changeset.Id;
    }

    private static List<(string Path, string Content)> TreeFixture()
    {
        List<(string Path, string Content)> files = [("README.md", "readme")];
        for (var i = 0; i < 10; i++)
        {
            files.Add(($"src/a_{i:D2}.txt", $"src content {i}"));
            files.Add(($"docs/b_{i:D2}.txt", $"docs content {i}"));
            files.Add(($"tests/t_{i:D2}.txt", $"tests content {i}"));
        }

        return files;
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

    public void Dispose() => harness.Dispose();
}
