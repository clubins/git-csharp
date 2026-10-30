using Scion.Cli.Local;

namespace Scion.Cli.Tests;

/// <summary>
/// The persisted sparse profile's file behavior: absence is the no-restriction default (null, not
/// an error), a saved profile round-trips its patterns, corrupt JSON fails with guidance, and
/// deletion is idempotent.
/// </summary>
public sealed class SparseProfileStateTests : IDisposable
{
    private readonly TempDirectory root = new();

    [Fact]
    public void Load_WithoutProfile_ReturnsNull()
    {
        Assert.Null(SparseProfileState.Load(root.Path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPatterns()
    {
        new SparseProfileState { Patterns = ["src/**", "docs/**/*.md"] }.Save(root.Path);

        var loaded = SparseProfileState.Load(root.Path);
        Assert.NotNull(loaded);
        Assert.Equal(["src/**", "docs/**/*.md"], loaded.Patterns);
    }

    [Fact]
    public void Load_CorruptJson_FailsWithGuidance()
    {
        Directory.CreateDirectory(Path.Combine(root.Path, ".scn"));
        File.WriteAllText(Path.Combine(root.Path, ".scn", "sparse.json"), "{ not json");

        var failure = Assert.Throws<ScionCliException>(() => SparseProfileState.Load(root.Path));
        Assert.Contains("sparse.json", failure.Message);
    }

    [Fact]
    public void Delete_RemovesTheProfile()
    {
        new SparseProfileState { Patterns = ["src/**"] }.Save(root.Path);

        SparseProfileState.Delete(root.Path);

        Assert.Null(SparseProfileState.Load(root.Path));
    }

    [Fact]
    public void Delete_WithoutProfile_IsNoOp()
    {
        SparseProfileState.Delete(root.Path);

        Assert.Null(SparseProfileState.Load(root.Path));
    }

    public void Dispose() => root.Dispose();
}
