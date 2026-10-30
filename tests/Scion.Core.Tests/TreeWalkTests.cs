using System.Collections.Immutable;
using System.Text;

namespace Scion.Core.Tests;

/// <summary>
/// The pure walk behind trees/missing: which blob hashes a snapshot references in a requested
/// path scope — deduplicated, normalized lowercase, ordinal-sorted — with the wanted-paths and
/// sparse-profile filters intersecting to narrow the scope.
/// </summary>
public sealed class TreeWalkTests
{
    [Fact]
    public void Collect_UnfilteredWalk_DedupsLowercasesAndSorts()
    {
        var shared = HashOf("shared");
        var snapshot = Snapshot(
            ("lib/x.txt", shared),
            ("docs/readme.md", shared), // same content at a second path — one hash
            ("src/a.cs", HashOf("b").ToUpperInvariant())); // mixed-case address, normalized on the way out

        var walked = TreeWalk.Collect(snapshot);

        List<string> expected = [shared, HashOf("b")];
        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, walked.BlobHashes);
    }

    [Fact]
    public void Collect_WantedPaths_RestrictTheScope()
    {
        var snapshot = Snapshot(
            ("lib/x.txt", HashOf("lib")),
            ("src/a.cs", HashOf("a")),
            ("src/deep/b.cs", HashOf("b")));

        var walked = TreeWalk.Collect(snapshot, ["src/**"]);

        List<string> expected = [HashOf("a"), HashOf("b")];
        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, walked.BlobHashes);
    }

    [Fact]
    public void Collect_SparseProfile_AloneRestrictsTheScope()
    {
        var snapshot = Snapshot(
            ("lib/x.txt", HashOf("lib")),
            ("src/a.cs", HashOf("a")),
            ("src/deep/b.cs", HashOf("b")));

        Assert.Equal([HashOf("b")], TreeWalk.Collect(snapshot, sparseProfile: ["src/deep/**"]).BlobHashes);
    }

    [Fact]
    public void Collect_BothFilters_Intersect()
    {
        var snapshot = Snapshot(
            ("lib/x.txt", HashOf("lib")),
            ("src/a.cs", HashOf("a")),
            ("src/deep/b.cs", HashOf("b")));

        // Either filter alone is broader; only the intersection survives, and order does not matter.
        Assert.Equal([HashOf("b")], TreeWalk.Collect(snapshot, ["src/**"], ["src/deep/**"]).BlobHashes);
        Assert.Equal([HashOf("b")], TreeWalk.Collect(snapshot, ["src/deep/**"], ["src/**"]).BlobHashes);
    }

    [Fact]
    public void Collect_NoMatchingPath_IsEmpty()
    {
        var snapshot = Snapshot(("lib/x.txt", HashOf("lib")));

        Assert.Empty(TreeWalk.Collect(snapshot, ["zzz/**"]).BlobHashes);
    }

    [Fact]
    public void Collect_EmptyTree_IsEmpty()
    {
        Assert.Empty(TreeWalk.Collect(TreeSnapshot.Empty).BlobHashes);
    }

    [Fact]
    public void Collect_WhitespacePattern_Throws()
    {
        var snapshot = Snapshot(("lib/x.txt", HashOf("lib")));

        Assert.Throws<ArgumentException>(() => TreeWalk.Collect(snapshot, [" "]));
    }

    private static string HashOf(string content)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(content)).Hash;
    }

    private static TreeSnapshot Snapshot(params (string Path, string BlobHash)[] entries)
    {
        var delta = entries
            .Select(e => new ChangeEntry(e.Path, null, e.BlobHash, FileContentKind.Regular, ChangeType.Added))
            .ToImmutableArray();
        return TreeSnapshot.Empty.Apply(delta);
    }
}
