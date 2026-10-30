namespace Scion.Core.Tests;

public class TreeSnapshotTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string BlobD = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Empty_ContainsNoPaths()
    {
        Assert.Equal(0, TreeSnapshot.Empty.Count);
        Assert.Empty(TreeSnapshot.Empty.Entries);
    }

    [Fact]
    public void FromChain_BuildsTreeFromRootChain()
    {
        var root = Changeset.Create(
            [],
            [Added("src/a.cs", BlobA), Added("docs/readme.md", BlobB)],
            "root",
            Author,
            Timestamp);
        var child = Changeset.Create([root.Id], [Added("src/b.cs", BlobC)], "child", Author, Timestamp);

        var tree = TreeSnapshot.FromChain([root, child]);

        Assert.Equal(3, tree.Count);
        Assert.Equal(BlobA, tree.Entries["src/a.cs"].BlobHash);
        Assert.Equal(BlobB, tree.Entries["docs/readme.md"].BlobHash);
        Assert.Equal(BlobC, tree.Entries["src/b.cs"].BlobHash);
    }

    [Fact]
    public void Entries_EnumerateInOrdinalPathOrder()
    {
        var tree = TreeSnapshot.Empty.Apply(
        [
            Added("z.cs", BlobA),
            Added("A.cs", BlobB),
            Added("a/mid.cs", BlobC),
            Added("a.cs", BlobD),
        ]);

        Assert.Equal(new[] { "A.cs", "a.cs", "a/mid.cs", "z.cs" }, tree.Entries.Keys);
    }

    [Fact]
    public void Apply_ReplaysAllChangeTypes()
    {
        var tree = TreeSnapshot.Empty.Apply(
        [
            Added("src/a.cs", BlobA),
            Added("src/b.cs", BlobB),
            Added("docs/readme.md", BlobC),
        ]);

        var next = tree.Apply(
        [
            Modified("src/a.cs", BlobA, BlobD),
            Deleted("docs/readme.md", BlobC),
            Deleted("src/b.cs", BlobB),
            Renamed("src/renamed.cs", BlobB, BlobB),
        ]);

        Assert.Equal(2, next.Count);
        Assert.Equal(BlobD, next.Entries["src/a.cs"].BlobHash);
        Assert.False(next.Entries.ContainsKey("docs/readme.md"));
        Assert.False(next.Entries.ContainsKey("src/b.cs"));
        Assert.Equal(BlobB, next.Entries["src/renamed.cs"].BlobHash);
    }

    [Fact]
    public void Apply_ProducesNewSnapshotAndKeepsOriginal()
    {
        var original = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);

        var next = original.Apply([Modified("a.cs", BlobA, BlobB)]);

        Assert.NotSame(original, next);
        Assert.Equal(BlobA, original.Entries["a.cs"].BlobHash);
        Assert.Equal(BlobB, next.Entries["a.cs"].BlobHash);
    }

    [Fact]
    public void Apply_EmptyDelta_ReturnsSameSnapshot()
    {
        var tree = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);

        Assert.Same(tree, tree.Apply([]));
    }

    [Fact]
    public void Apply_Changeset_EqualsApplyingItsDelta()
    {
        var root = Changeset.Create([], [Added("a.cs", BlobA)], "root", Author, Timestamp);

        Assert.Equal(TreeSnapshot.Empty.Apply(root), TreeSnapshot.Empty.Apply(root.Changes));
    }

    [Fact]
    public void Apply_RenamedEntry_UpsertsNewPathOnly()
    {
        // The rename convention: the pre-rename path is removed by its own Deleted entry, so a
        // Renamed entry on its own touches only the post-rename path.
        var tree = TreeSnapshot.Empty.Apply([Added("src/old.cs", BlobA), Added("src/other.cs", BlobB)]);

        var next = tree.Apply([Renamed("src/new.cs", BlobA, BlobA)]);

        Assert.True(next.Entries.ContainsKey("src/old.cs"));
        Assert.Equal(BlobA, next.Entries["src/new.cs"].BlobHash);
    }

    [Fact]
    public void Apply_KindChange_SameBlob_UpdatesKind()
    {
        var tree = TreeSnapshot.Empty.Apply([Added("run.sh", BlobA, FileContentKind.Regular)]);

        var next = tree.Apply([Modified("run.sh", BlobA, BlobA, FileContentKind.Executable)]);

        Assert.Equal(BlobA, next.Entries["run.sh"].BlobHash);
        Assert.Equal(FileContentKind.Executable, next.Entries["run.sh"].Kind);
    }

    [Fact]
    public void TryGet_ReturnsMappingAndAbsence()
    {
        var tree = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);

        Assert.True(tree.TryGet("a.cs", out var entry));
        Assert.Equal(BlobA, entry!.BlobHash);
        Assert.False(tree.TryGet("missing.cs", out var absent));
        Assert.Null(absent);
    }

    [Fact]
    public void FromChain_EquivalentChains_CompareEqual()
    {
        // Reaching the same tree in two changesets versus one must compare — and hash — equal.
        var first = Changeset.Create([], [Added("src/a.cs", BlobA), Added("src/b.cs", BlobB)], "root", Author, Timestamp);
        var second = Changeset.Create([first.Id], [Modified("src/a.cs", BlobA, BlobC)], "tip", Author, Timestamp);
        var single = Changeset.Create([], [Added("src/a.cs", BlobC), Added("src/b.cs", BlobB)], "same tree", Author, Timestamp);

        var replayed = TreeSnapshot.FromChain([first, second]);
        var direct = TreeSnapshot.FromChain([single]);

        Assert.Equal(replayed, direct);
        Assert.Equal(replayed.Id, direct.Id);
    }

    [Fact]
    public void Id_DifferentTrees_Differ()
    {
        var a = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);
        var b = TreeSnapshot.Empty.Apply([Added("a.cs", BlobB)]);

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Id_Is64LowercaseHexCharacters()
    {
        Assert.Matches("^[0-9a-f]{64}$", TreeSnapshot.Empty.Id);
    }

    [Fact]
    public void Apply_AddedPathAlreadyInTree_Throws()
    {
        var tree = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);

        Assert.Throws<ArgumentException>(() => tree.Apply([Added("a.cs", BlobB)]));
    }

    [Fact]
    public void Apply_ModifiedMissingPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => TreeSnapshot.Empty.Apply([Modified("a.cs", BlobA, BlobB)]));
    }

    [Fact]
    public void Apply_DeletedMissingPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => TreeSnapshot.Empty.Apply([Deleted("a.cs", BlobA)]));
    }

    [Fact]
    public void Apply_OldBlobMismatch_Throws()
    {
        var tree = TreeSnapshot.Empty.Apply([Added("a.cs", BlobA)]);

        Assert.Throws<ArgumentException>(() => tree.Apply([Modified("a.cs", BlobB, BlobC)]));
    }

    [Fact]
    public void Apply_AddedEntryWithOldBlob_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TreeSnapshot.Empty.Apply([new ChangeEntry("a.cs", BlobA, BlobB, FileContentKind.Regular, ChangeType.Added)]));
    }

    [Fact]
    public void Apply_DeletedEntryWithNewBlob_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TreeSnapshot.Empty.Apply([new ChangeEntry("a.cs", BlobA, BlobA, FileContentKind.Regular, ChangeType.Deleted)]));
    }

    [Fact]
    public void Apply_UnknownChangeType_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TreeSnapshot.Empty.Apply([new ChangeEntry("a.cs", null, BlobA, FileContentKind.Regular, (ChangeType)99)]));
    }

    [Fact]
    public void FromChain_RejectsNonRootFirstChangeset()
    {
        var root = Changeset.Create([], [Added("a.cs", BlobA)], "root", Author, Timestamp);
        var child = Changeset.Create([root.Id], [Modified("a.cs", BlobA, BlobB)], "child", Author, Timestamp);

        Assert.Throws<ArgumentException>(() => TreeSnapshot.FromChain([child]));
    }

    [Fact]
    public void FromChain_SecondRootMidChain_Throws()
    {
        var root = Changeset.Create([], [Added("a.cs", BlobA)], "root", Author, Timestamp);
        var unrelatedRoot = Changeset.Create([], [Added("b.cs", BlobB)], "other root", Author, Timestamp);

        Assert.Throws<ArgumentException>(() => TreeSnapshot.FromChain([root, unrelatedRoot]));
    }

    [Fact]
    public void FromChain_FirstParentMismatch_Throws()
    {
        var baseCs = Changeset.Create([], [Added("a.cs", BlobA)], "base", Author, Timestamp);
        var branchOne = Changeset.Create([baseCs.Id], [Modified("a.cs", BlobA, BlobB)], "one", Author, Timestamp);
        var branchTwo = Changeset.Create([baseCs.Id], [Modified("a.cs", BlobA, BlobC)], "two", Author, Timestamp);

        Assert.Throws<ArgumentException>(() => TreeSnapshot.FromChain([baseCs, branchOne, branchTwo]));
    }

    [Fact]
    public void FromChain_OntoBaseTree_MatchesFullReplay()
    {
        var root = Changeset.Create([], [Added("src/a.cs", BlobA), Added("src/b.cs", BlobB)], "root", Author, Timestamp);
        var child = Changeset.Create([root.Id], [Modified("src/a.cs", BlobA, BlobC)], "child", Author, Timestamp);

        var full = TreeSnapshot.FromChain([root, child]);
        var resumed = TreeSnapshot.FromChain(TreeSnapshot.FromChain([root]), [child]);

        Assert.Equal(full, resumed);
    }

    [Fact]
    public void FromChain_OntoBaseTree_RejectsRootFirstChangeset()
    {
        var root = Changeset.Create([], [Added("a.cs", BlobA)], "root", Author, Timestamp);

        Assert.Throws<ArgumentException>(() => TreeSnapshot.FromChain(TreeSnapshot.Empty, [root]));
    }

    [Fact]
    public void FromChain_MergeChangeset_AppliesItsFirstParentDelta()
    {
        var baseCs = Changeset.Create([], [Added("src/a.cs", BlobA)], "base", Author, Timestamp);
        var first = Changeset.Create([baseCs.Id], [Added("src/first.cs", BlobB)], "first", Author, Timestamp);
        var second = Changeset.Create([baseCs.Id], [Added("src/second.cs", BlobC)], "second", Author, Timestamp);
        var merge = Changeset.Create([first.Id, second.Id], [Modified("src/a.cs", BlobA, BlobD)], "merge", Author, Timestamp);

        var tree = TreeSnapshot.FromChain([baseCs, first, merge]);

        Assert.Equal(2, tree.Count);
        Assert.Equal(BlobD, tree.Entries["src/a.cs"].BlobHash);
        Assert.Equal(BlobB, tree.Entries["src/first.cs"].BlobHash);
        Assert.False(tree.Entries.ContainsKey("src/second.cs"));
    }

    private static ChangeEntry Added(string path, string newBlob, FileContentKind kind = FileContentKind.Regular)
    {
        return new ChangeEntry(path, null, newBlob, kind, ChangeType.Added);
    }

    private static ChangeEntry Modified(string path, string oldBlob, string newBlob, FileContentKind kind = FileContentKind.Regular)
    {
        return new ChangeEntry(path, oldBlob, newBlob, kind, ChangeType.Modified);
    }

    private static ChangeEntry Deleted(string path, string oldBlob)
    {
        return new ChangeEntry(path, oldBlob, null, FileContentKind.Regular, ChangeType.Deleted);
    }

    private static ChangeEntry Renamed(string newPath, string oldBlob, string newBlob, FileContentKind kind = FileContentKind.Regular)
    {
        return new ChangeEntry(newPath, oldBlob, newBlob, kind, ChangeType.Renamed);
    }
}
