using System.Collections.Immutable;

namespace Scion.Core.Tests;

/// <summary>
/// Unit tests for tree diff plus randomized property tests against a naive reference. The naive
/// reference deliberately uses a different algorithm than <see cref="TreeDiff"/> — per-path
/// lookups over the path union plus a linear-scan rename pass instead of a merge sweep with
/// hash-bucketed pairing — so agreement between the two is evidence about the semantics, not a
/// tautology. Every random pair must satisfy: the diff equals the reference element-wise, the
/// delta round-trips in both directions (<c>before.Apply(diff(before, after)) == after</c> and
/// back), and the output stays in canonical path order. Seeds are fixed so any failure is
/// reproducible.
/// </summary>
public class TreeDiffTests
{
    private const long Timestamp = 1_790_000_000;
    private const int Seed = 20261030;
    private const int HashPoolSize = 6;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string BlobD = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";
    private const string BlobE = "0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Diff_IdenticalTrees_IsEmpty()
    {
        var root = Changeset.Create([], [Added("src/a.cs", BlobA), Added("docs/readme.md", BlobB)], "root", Author, Timestamp);
        var child = Changeset.Create([root.Id], [Modified("src/a.cs", BlobA, BlobC)], "child", Author, Timestamp);
        var full = TreeSnapshot.FromChain([root, child]);
        var resumed = TreeSnapshot.FromChain(TreeSnapshot.FromChain([root]), [child]);

        Assert.Empty(TreeDiff.Diff(full, full));
        Assert.Empty(TreeDiff.Diff(full, resumed));
        Assert.Empty(TreeDiff.Diff(TreeSnapshot.Empty, TreeSnapshot.Empty));
    }

    [Fact]
    public void Diff_EmptyToTree_AllAdded()
    {
        var after = TreeSnapshot.Empty.Apply([Added("src/a.cs", BlobA, FileContentKind.Regular), Added("run.sh", BlobB, FileContentKind.Executable)]);

        var diff = TreeDiff.Diff(TreeSnapshot.Empty, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("run.sh", null, BlobB, FileContentKind.Executable, ChangeType.Added),
                new ChangeEntry("src/a.cs", null, BlobA, FileContentKind.Regular, ChangeType.Added),
            },
            diff);
        Assert.Equal(after, TreeSnapshot.Empty.Apply(diff));
    }

    [Fact]
    public void Diff_TreeToEmpty_AllDeleted()
    {
        var before = TreeSnapshot.Empty.Apply([Added("src/a.cs", BlobA), Added("run.sh", BlobB)]);

        var diff = TreeDiff.Diff(before, TreeSnapshot.Empty);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("run.sh", BlobB, null, FileContentKind.Regular, ChangeType.Deleted),
                new ChangeEntry("src/a.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(TreeSnapshot.Empty, before.Apply(diff));
    }

    [Fact]
    public void Diff_AddModifyDelete_ClassifiesEachPath()
    {
        var before = TreeSnapshot.Empty.Apply(
            [Added("a.cs", BlobA), Added("b.cs", BlobB), Added("c.cs", BlobC)]);
        var after = TreeSnapshot.Empty.Apply(
            [Added("b.cs", BlobB), Added("c.cs", BlobD), Added("d.cs", BlobE)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("a.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
                new ChangeEntry("c.cs", BlobC, BlobD, FileContentKind.Regular, ChangeType.Modified),
                new ChangeEntry("d.cs", null, BlobE, FileContentKind.Regular, ChangeType.Added),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_KindOnlyChange_IsModifiedWithSameBlobHash()
    {
        var before = TreeSnapshot.Empty.Apply([Added("run.sh", BlobA, FileContentKind.Regular)]);
        var after = TreeSnapshot.Empty.Apply([Added("run.sh", BlobA, FileContentKind.Executable)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[] { new ChangeEntry("run.sh", BlobA, BlobA, FileContentKind.Executable, ChangeType.Modified) },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_Rename_EmitsRenamedAndDeletedInCanonicalOrder()
    {
        // "src/new.cs" sorts before "src/old.cs" ordinally, so the Renamed entry precedes the
        // Deleted entry of its own pair.
        var before = TreeSnapshot.Empty.Apply([Added("src/old.cs", BlobA), Added("keep.cs", BlobB)]);
        var after = TreeSnapshot.Empty.Apply([Added("src/new.cs", BlobA), Added("keep.cs", BlobB)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("src/new.cs", BlobA, BlobA, FileContentKind.Regular, ChangeType.Renamed),
                new ChangeEntry("src/old.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_RenameWithKindChange_RenamedCarriesNewKind()
    {
        var before = TreeSnapshot.Empty.Apply([Added("src/old.cs", BlobA, FileContentKind.Regular)]);
        var after = TreeSnapshot.Empty.Apply([Added("src/new.cs", BlobA, FileContentKind.Executable)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("src/new.cs", BlobA, BlobA, FileContentKind.Executable, ChangeType.Renamed),
                new ChangeEntry("src/old.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_DifferentHashes_IsNotARename()
    {
        var before = TreeSnapshot.Empty.Apply([Added("old.cs", BlobA)]);
        var after = TreeSnapshot.Empty.Apply([Added("new.cs", BlobB)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("new.cs", null, BlobB, FileContentKind.Regular, ChangeType.Added),
                new ChangeEntry("old.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_SharedHashMoves_PairInOrdinalOrder()
    {
        var before = TreeSnapshot.Empty.Apply([Added("d/b.cs", BlobA), Added("d/a.cs", BlobA), Added("stay.cs", BlobB)]);
        var after = TreeSnapshot.Empty.Apply([Added("n/2.cs", BlobA), Added("n/1.cs", BlobA), Added("stay.cs", BlobB)]);

        var diff = TreeDiff.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ChangeEntry("d/a.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
                new ChangeEntry("d/b.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
                new ChangeEntry("n/1.cs", BlobA, BlobA, FileContentKind.Regular, ChangeType.Renamed),
                new ChangeEntry("n/2.cs", BlobA, BlobA, FileContentKind.Regular, ChangeType.Renamed),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_RenameIsOneToOne()
    {
        var before = TreeSnapshot.Empty.Apply([Added("d1.cs", BlobA)]);
        var after = TreeSnapshot.Empty.Apply([Added("a1.cs", BlobA), Added("a2.cs", BlobA)]);

        var diff = TreeDiff.Diff(before, after);

        // The first added path in ordinal order pairs with the deletion; the surplus stays Added.
        Assert.Equal(
            new[]
            {
                new ChangeEntry("a1.cs", BlobA, BlobA, FileContentKind.Regular, ChangeType.Renamed),
                new ChangeEntry("a2.cs", null, BlobA, FileContentKind.Regular, ChangeType.Added),
                new ChangeEntry("d1.cs", BlobA, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_DeepNestedPaths_CoversEveryLevel()
    {
        var before = TreeSnapshot.Empty.Apply(
        [
            Added("a/b/c/d/e/leaf.cs", BlobA),
            Added("a/b/dead.cs", BlobB),
            Added("a/keep.cs", BlobC),
        ]);
        var after = TreeSnapshot.Empty.Apply(
        [
            Added("a/b/c/d/e/leaf.cs", BlobD),
            Added("a/b/c/new.cs", BlobB),
            Added("a/keep.cs", BlobC),
            Added("a/b/c/d/e/f/g/deep.cs", BlobE),
        ]);

        var diff = TreeDiff.Diff(before, after);

        // "a/b/dead.cs" and "a/b/c/new.cs" share BlobB, so the move is detected as a rename.
        Assert.Equal(
            new[]
            {
                new ChangeEntry("a/b/c/d/e/f/g/deep.cs", null, BlobE, FileContentKind.Regular, ChangeType.Added),
                new ChangeEntry("a/b/c/d/e/leaf.cs", BlobA, BlobD, FileContentKind.Regular, ChangeType.Modified),
                new ChangeEntry("a/b/c/new.cs", BlobB, BlobB, FileContentKind.Regular, ChangeType.Renamed),
                new ChangeEntry("a/b/dead.cs", BlobB, null, FileContentKind.Regular, ChangeType.Deleted),
            },
            diff);
        Assert.Equal(after, before.Apply(diff));
    }

    [Fact]
    public void Diff_DeltaSurvivesChangesetCanonicalization()
    {
        var parent = Changeset.Create([], [Added("src/a.cs", BlobA), Added("src/b.cs", BlobB)], "parent", Author, Timestamp);
        var parentTree = TreeSnapshot.FromChain([parent]);
        var childTree = parentTree.Apply([Modified("src/a.cs", BlobA, BlobC), Deleted("src/b.cs", BlobB)]);
        var delta = TreeDiff.Diff(parentTree, childTree);

        var child = Changeset.Create([parent.Id], delta, "child", Author, Timestamp);

        Assert.Equal(childTree, parentTree.Apply(child.Changes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(120)]
    public void RandomPairs_MatchNaiveReferenceRoundTripAndStayCanonical(int treeSize)
    {
        for (var trial = 0; trial < 8; trial++)
        {
            var random = new Random(Seed + (treeSize * 1_000) + trial);
            var hashes = HashPool(random);
            var before = RandomTree(random, treeSize, hashes);
            var after = Mutate(random, before, hashes);

            var diff = TreeDiff.Diff(before, after);
            Assert.True(diff.SequenceEqual(NaiveDiff(before, after)), $"reference mismatch at trial {trial}");
            Assert.True(diff.SequenceEqual(TreeDiff.Diff(before, after)), $"nondeterministic at trial {trial}");
            AssertCanonical(diff);
            Assert.Equal(after, before.Apply(diff));

            var reverse = TreeDiff.Diff(after, before);
            Assert.True(reverse.SequenceEqual(NaiveDiff(after, before)), $"reverse reference mismatch at trial {trial}");
            AssertCanonical(reverse);
            Assert.Equal(before, after.Apply(reverse));
        }
    }

    private static void AssertCanonical(ImmutableArray<ChangeEntry> diff)
    {
        for (var i = 1; i < diff.Length; i++)
        {
            Assert.True(
                string.CompareOrdinal(diff[i - 1].Path, diff[i].Path) < 0,
                $"diff output must be in canonical path order: '{diff[i - 1].Path}' then '{diff[i].Path}'");
        }
    }

    // ---- Random pair generation -------------------------------------------------------

    private static readonly string[] Segments = ["src", "lib", "core", "a", "b", "deep", "x", "docs", "tests", "gen"];

    private static string[] HashPool(Random random)
    {
        return Enumerable.Range(0, HashPoolSize).Select(_ => RandomHash(random)).ToArray();
    }

    private static string RandomHash(Random random)
    {
        return string.Concat(Enumerable.Range(0, 8).Select(_ => random.Next().ToString("x8")));
    }

    private static string RandomPath(Random random)
    {
        var depth = random.Next(1, 7);
        return string.Join('/', Enumerable.Range(0, depth).Select(_ => Segments[random.Next(Segments.Length)]));
    }

    private static TreeSnapshot RandomTree(Random random, int pathCount, string[] hashes)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (paths.Count < pathCount)
        {
            paths.Add(RandomPath(random));
        }

        var delta = paths
            .Select(p => new ChangeEntry(p, null, hashes[random.Next(hashes.Length)], (FileContentKind)random.Next(3), ChangeType.Added))
            .ToImmutableArray();
        return TreeSnapshot.Empty.Apply(delta);
    }

    /// <summary>
    /// Derives a second snapshot from <paramref name="before"/> with random deletes, modifies
    /// (hash, kind, or both), moves (same blob at a fresh path — rename candidates), and fresh
    /// additions, so realistic mixes of every change class appear in the pairs.
    /// </summary>
    private static TreeSnapshot Mutate(Random random, TreeSnapshot before, string[] hashes)
    {
        var entries = before.Entries.ToBuilder();
        var live = new HashSet<string>(before.Entries.Keys, StringComparer.Ordinal);
        var additions = new List<ChangeEntry>();

        foreach (var (path, entry) in before.Entries)
        {
            switch (random.Next(4))
            {
                case 0:
                    entries.Remove(path);
                    live.Remove(path);
                    break;
                case 1:
                    var newHash = random.Next(2) == 0 ? entry.BlobHash : hashes[random.Next(hashes.Length)];
                    var newKind = random.Next(2) == 0 ? entry.Kind : (FileContentKind)random.Next(3);
                    entries[path] = new TreeEntry(newHash, newKind);
                    break;
                case 2:
                    entries.Remove(path);
                    live.Remove(path);
                    if (TryFreshPath(random, live, out var movedTo))
                    {
                        additions.Add(new ChangeEntry(movedTo, null, entry.BlobHash, entry.Kind, ChangeType.Added));
                    }

                    break;
            }
        }

        var freshAdds = random.Next(0, 4);
        for (var i = 0; i < freshAdds; i++)
        {
            if (TryFreshPath(random, live, out var path))
            {
                additions.Add(new ChangeEntry(path, null, hashes[random.Next(hashes.Length)], (FileContentKind)random.Next(3), ChangeType.Added));
            }
        }

        return new TreeSnapshot(entries.ToImmutable()).Apply(additions.ToImmutableArray());
    }

    /// <summary>Claims and returns a path not in <paramref name="taken"/>, or false after 8 attempts.</summary>
    private static bool TryFreshPath(Random random, HashSet<string> taken, out string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = RandomPath(random);
            if (taken.Add(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    // ---- Naive reference implementation ------------------------------------------------

    /// <summary>
    /// The naive reference: classify every path of the union by independent lookups (no merge
    /// sweep), then pair renames by a linear scan over the additions (no hash buckets). Pairing
    /// order — deletions in path order each taking the first unpaired matching addition in path
    /// order — is the documented policy both implementations must agree on.
    /// </summary>
    private static List<ChangeEntry> NaiveDiff(TreeSnapshot before, TreeSnapshot after)
    {
        var paths = before.Entries.Keys.Union(after.Entries.Keys).ToList();
        paths.Sort(StringComparer.Ordinal);

        var deletions = new List<(string Path, string BlobHash, FileContentKind Kind)>();
        var additions = new List<(string Path, string BlobHash, FileContentKind Kind)>();
        var modifications = new List<ChangeEntry>();
        foreach (var path in paths)
        {
            before.TryGet(path, out var beforeEntry);
            after.TryGet(path, out var afterEntry);
            switch (beforeEntry, afterEntry)
            {
                case (null, not null):
                    additions.Add((path, afterEntry.BlobHash, afterEntry.Kind));
                    break;
                case (not null, null):
                    deletions.Add((path, beforeEntry.BlobHash, beforeEntry.Kind));
                    break;
                case (not null, not null) when !beforeEntry.Equals(afterEntry):
                    modifications.Add(new ChangeEntry(path, beforeEntry.BlobHash, afterEntry.BlobHash, afterEntry.Kind, ChangeType.Modified));
                    break;
            }
        }

        var consumed = new bool[additions.Count];
        var result = new List<ChangeEntry>(additions.Count + deletions.Count + modifications.Count);
        foreach (var (path, hash, kind) in deletions)
        {
            result.Add(new ChangeEntry(path, hash, null, kind, ChangeType.Deleted));
            for (var i = 0; i < additions.Count; i++)
            {
                if (consumed[i] || !string.Equals(additions[i].BlobHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                consumed[i] = true;
                result.Add(new ChangeEntry(additions[i].Path, hash, additions[i].BlobHash, additions[i].Kind, ChangeType.Renamed));
                break;
            }
        }

        for (var i = 0; i < additions.Count; i++)
        {
            if (!consumed[i])
            {
                result.Add(new ChangeEntry(additions[i].Path, null, additions[i].BlobHash, additions[i].Kind, ChangeType.Added));
            }
        }

        result.AddRange(modifications);
        result.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        return result;
    }

    // ---- Test helpers ------------------------------------------------------------------

    private static ChangeEntry Added(string path, string newBlob, FileContentKind kind = FileContentKind.Regular)
    {
        return new ChangeEntry(path, null, newBlob, kind, ChangeType.Added);
    }

    private static ChangeEntry Deleted(string path, string oldBlob)
    {
        return new ChangeEntry(path, oldBlob, null, FileContentKind.Regular, ChangeType.Deleted);
    }

    private static ChangeEntry Modified(string path, string oldBlob, string newBlob, FileContentKind kind = FileContentKind.Regular)
    {
        return new ChangeEntry(path, oldBlob, newBlob, kind, ChangeType.Modified);
    }
}
