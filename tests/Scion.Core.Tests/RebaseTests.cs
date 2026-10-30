using System.Collections.Immutable;
using System.Text;

namespace Scion.Core.Tests;

/// <summary>
/// Unit tests for rebase-as-replay (<see cref="Rebase.Replay"/>). Scenarios are built from
/// path-to-content maps and <see cref="TreeDiff"/>-derived deltas, so chains carry the same
/// canonical encoding production callers get; expected final trees come from replaying the
/// rebased chain with <see cref="TreeSnapshot.FromChain(TreeSnapshot, System.Collections.Generic.IEnumerable{Changeset})"/>.
/// </summary>
public class RebaseTests
{
    private static readonly Author TestAuthor = new("Rebase Tests", "rebase@test.local");

    private const long Timestamp = 1_700_000_000;

    // ---- Linear replay: equivalence of final trees -----------------------------------------

    [Fact]
    public void Replay_OntoIdenticalBaseTree_PreservesDeltasMetadataAndFinalTree()
    {
        var rootTree = Tree(("hello.txt", "hello\n"), ("keep.txt", "keep\n"));
        var root = Root(rootTree, "root");
        var c1Tree = Tree(("hello.txt", "hello\n"), ("keep.txt", "keep\n"), ("feature.txt", "feature\n"));
        var c2Tree = Tree(("hello.txt", "hello\n"), ("keep.txt", "keep v2\n"), ("feature.txt", "feature\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "add feature");
        var c2 = Commit(c1.Id, c1Tree, c2Tree, "edit keep");
        var originalFinal = TreeSnapshot.FromChain([root, c1, c2]);

        // An independent root whose content matches: same tree, different id — content addressing.
        var twinTree = Tree(("hello.txt", "hello\n"), ("keep.txt", "keep\n"));
        var twinBase = Root(twinTree, "twin root");
        Assert.NotEqual(root.Id, twinBase.Id);

        var result = Rebase.Replay(
            rootTree, [c1, c2], twinBase.Id, twinTree,
            Lookup("hello\n", "keep\n", "keep v2\n", "feature\n"));

        Assert.True(result.IsClean);
        Assert.Empty(result.Conflicts);
        Assert.Equal(2, result.Rebased.Length);

        // Lineage: first parent is the new base, the rest first-parent linked.
        Assert.Equal(twinBase.Id, result.Rebased[0].Parents[0]);
        Assert.Equal(result.Rebased[0].Id, result.Rebased[1].Parents[0]);
        Assert.NotEqual(c1.Id, result.Rebased[0].Id);

        // Deltas survive byte-for-byte (both sides canonical), metadata carries over.
        Changeset[] chain = [c1, c2];
        Assert.True(chain[0].Changes.SequenceEqual(result.Rebased[0].Changes));
        Assert.True(chain[1].Changes.SequenceEqual(result.Rebased[1].Changes));
        for (var i = 0; i < chain.Length; i++)
        {
            Assert.Equal(chain[i].Message, result.Rebased[i].Message);
            Assert.Equal(TestAuthor, result.Rebased[i].Author);
            Assert.Equal(Timestamp, result.Rebased[i].TimestampUtc);
        }

        // The rebased chain replays to the original final tree.
        var rebasedFinal = TreeSnapshot.FromChain(twinTree, result.Rebased);
        Assert.Equal(originalFinal, rebasedFinal);
    }

    // ---- Branched replay: clean composition --------------------------------------------------

    [Fact]
    public void Replay_OntoDisjointBase_ComposesBothSides()
    {
        var rootTree = Tree(("src/a.txt", "a1\n"), ("src/b.txt", "b1\n"), ("keep.txt", "k\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("src/a.txt", "a2\n"), ("src/b.txt", "b1\n"), ("keep.txt", "k\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit a on base");
        var c1Tree = Tree(("src/a.txt", "a1\n"), ("src/b.txt", "b1\n"), ("keep.txt", "k\n"), ("feature.txt", "f\n"));
        var c2Tree = Tree(("src/a.txt", "a1\n"), ("src/b.txt", "b2\n"), ("keep.txt", "k\n"), ("feature.txt", "f\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "add feature");
        var c2 = Commit(c1.Id, c1Tree, c2Tree, "edit b");

        var result = Rebase.Replay(
            rootTree, [c1, c2], b.Id, bTree,
            Lookup("a1\n", "a2\n", "b1\n", "b2\n", "k\n", "f\n"));

        Assert.True(result.IsClean);
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "src/a.txt", "a2\n"); // the new base's edit stands
        AssertContent(final, "src/b.txt", "b2\n"); // the replayed edit lands
        AssertContent(final, "keep.txt", "k\n");
        AssertContent(final, "feature.txt", "f\n"); // the replayed add lands
    }

    [Fact]
    public void Replay_RenameCarriesTheNewBaseEdit()
    {
        var rootTree = Tree(("src/old.txt", "v1\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("src/old.txt", "v2\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit old on base");
        var renamedTree = Tree(("src/new.txt", "v1\n"));
        var c1 = Commit(root.Id, rootTree, renamedTree, "rename old to new");

        var result = Rebase.Replay(rootTree, [c1], b.Id, bTree, Lookup("v1\n", "v2\n"));

        Assert.True(result.IsClean);
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "src/new.txt", "v2\n"); // the base's edit rides the rename
        AssertAbsent(final, "src/old.txt");

        // The rebased delta keeps the rename convention, against the new blob.
        var changes = result.Rebased[0].Changes;
        Assert.Equal(2, changes.Length);
        Assert.Contains(changes, change => change.Type == ChangeType.Deleted && change.Path == "src/old.txt");
        Assert.Contains(changes, change => change.Type == ChangeType.Renamed && change.Path == "src/new.txt");
    }

    [Fact]
    public void Replay_MergeChangeset_LinearizesToFirstParentDelta()
    {
        var rootTree = Tree(("hello.txt", "h\n"), ("keep.txt", "k\n"));
        var root = Root(rootTree, "root");
        var aTree = Tree(("hello.txt", "h+A\n"), ("keep.txt", "k\n"));
        var a = Commit(root.Id, rootTree, aTree, "edit hello");
        var sTree = Tree(("hello.txt", "h\n"), ("keep.txt", "k+S\n"));
        var s = Commit(root.Id, rootTree, sTree, "edit keep on sibling");
        var mTree = Tree(("hello.txt", "h+A\n"), ("keep.txt", "k+M\n"));
        var m = MergeCommit(a.Id, aTree, s.Id, mTree, "merge sibling");
        var bTree = Tree(("hello.txt", "h\n"), ("keep.txt", "k\n"), ("base.txt", "b\n"));
        var b = Commit(root.Id, rootTree, bTree, "add base.txt");

        var result = Rebase.Replay(
            rootTree, [a, m], b.Id, bTree,
            Lookup("h\n", "h+A\n", "k\n", "k+S\n", "k+M\n", "b\n"));

        Assert.True(result.IsClean);
        var a1 = result.Rebased[0];
        var m1 = result.Rebased[1];

        // The merge rebases to a non-merge: the second parent is dropped, the first-parent
        // delta (which already carries the merged content) is what replays.
        Assert.Single(a1.Parents);
        Assert.Equal(b.Id, a1.Parents[0]);
        Assert.Single(m1.Parents);
        Assert.Equal(a1.Id, m1.Parents[0]);

        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "hello.txt", "h+A\n");
        AssertContent(final, "keep.txt", "k+M\n");
        AssertContent(final, "base.txt", "b\n");
    }

    // ---- Conflict surfacing -------------------------------------------------------------------

    [Fact]
    public void Replay_ConflictingEdit_SurfacesConflictAndKeepsTheNewBase()
    {
        var rootTree = Tree(("hello.txt", "line1\nline2\n"), ("keep.txt", "k\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("hello.txt", "base line1\nline2\n"), ("keep.txt", "k\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit hello on base");
        var c1Tree = Tree(("hello.txt", "chain line1\nline2\n"), ("keep.txt", "k\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "edit hello on chain");

        var result = Rebase.Replay(
            rootTree, [c1], b.Id, bTree,
            Lookup("line1\nline2\n", "base line1\nline2\n", "chain line1\nline2\n", "k\n"));

        Assert.False(result.IsClean);
        var rebaseConflict = Assert.Single(result.Conflicts);
        Assert.Equal(0, rebaseConflict.SourceIndex);
        Assert.Equal(c1.Id, rebaseConflict.SourceId);
        Assert.Equal(result.Rebased[0].Id, rebaseConflict.RebasedId);
        Assert.Equal(MergeConflictKind.Content, rebaseConflict.Conflict.Kind);
        Assert.Equal("hello.txt", rebaseConflict.Conflict.Path);
        var marked = rebaseConflict.Conflict.MergedContent;
        Assert.NotNull(marked);
        Assert.Contains("<<<<<<< ours\n", marked!, StringComparison.Ordinal);
        Assert.Contains("base line1\n", marked!, StringComparison.Ordinal);
        Assert.Contains("chain line1\n", marked!, StringComparison.Ordinal);

        // Ours wins: the new base's version stands, so the rebased delta is empty — the
        // conflicted path contributes no change, and no blob was synthesized.
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "hello.txt", "base line1\nline2\n");
        Assert.True(result.Rebased[0].Changes.IsEmpty);
        Assert.Empty(result.MergedBlobs);
    }

    [Fact]
    public void Replay_ContinuesAfterConflict()
    {
        var rootTree = Tree(("hello.txt", "line1\nline2\n"), ("keep.txt", "k\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("hello.txt", "base line1\nline2\n"), ("keep.txt", "k\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit hello on base");
        var c1Tree = Tree(("hello.txt", "chain line1\nline2\n"), ("keep.txt", "k\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "edit hello on chain");
        var c2Tree = Tree(("hello.txt", "chain line1\nline2\n"), ("keep.txt", "k\n"), ("other.txt", "o\n"));
        var c2 = Commit(c1.Id, c1Tree, c2Tree, "add other");

        var result = Rebase.Replay(
            rootTree, [c1, c2], b.Id, bTree,
            Lookup("line1\nline2\n", "base line1\nline2\n", "chain line1\nline2\n", "k\n", "o\n"));

        // The replay did not abort: one conflict at step 0, step 1 rebased cleanly on top.
        Assert.Equal(2, result.Rebased.Length);
        var rebaseConflict = Assert.Single(result.Conflicts);
        Assert.Equal(0, rebaseConflict.SourceIndex);
        Assert.Equal(result.Rebased[0].Id, result.Rebased[1].Parents[0]);
        Assert.Equal("add other", result.Rebased[1].Message);

        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "hello.txt", "base line1\nline2\n");
        AssertContent(final, "keep.txt", "k\n");
        AssertContent(final, "other.txt", "o\n");
        var added = Assert.Single(result.Rebased[1].Changes);
        Assert.Equal(new ChangeEntry("other.txt", null, HashOf("o\n"), FileContentKind.Regular, ChangeType.Added), added);
    }

    [Fact]
    public void Replay_DeleteVersusModify_SurfacesKindAndKeepsTheNewBase()
    {
        var rootTree = Tree(("a.txt", "v1\n"), ("b.txt", "b\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("b.txt", "b\n")); // the new base deleted a.txt
        var b = Commit(root.Id, rootTree, bTree, "delete a");
        var c1Tree = Tree(("a.txt", "v2\n"), ("b.txt", "b\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "modify a");

        var result = Rebase.Replay(rootTree, [c1], b.Id, bTree, Lookup("v1\n", "v2\n", "b\n"));

        Assert.False(result.IsClean);
        var rebaseConflict = Assert.Single(result.Conflicts);
        Assert.Equal(MergeConflictKind.DeleteModify, rebaseConflict.Conflict.Kind);
        Assert.Equal("a.txt", rebaseConflict.Conflict.Path);

        // Ours' deletion stands.
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertAbsent(final, "a.txt");
        AssertContent(final, "b.txt", "b\n");
        Assert.True(result.Rebased[0].Changes.IsEmpty);
    }

    // ---- Empty deltas and synthesized blobs -----------------------------------------------------

    [Fact]
    public void Replay_ChangeAlreadyOnTheNewBase_RebasesToAnEmptyDelta()
    {
        var rootTree = Tree(("hello.txt", "v1\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("hello.txt", "v2\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit hello");
        var c1Tree = Tree(("hello.txt", "v2\n")); // the chain made the same change
        var c1 = Commit(root.Id, rootTree, c1Tree, "edit hello too");

        var result = Rebase.Replay(rootTree, [c1], b.Id, bTree, Lookup("v1\n", "v2\n"));

        Assert.True(result.IsClean);
        Assert.True(result.Rebased[0].Changes.IsEmpty);
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        Assert.Equal(bTree, final);
    }

    [Fact]
    public void Replay_CleanLineMerge_SynthesizesABlobKeyedByHash()
    {
        var rootTree = Tree(("notes.txt", "line1\nline2\nline3\n"));
        var root = Root(rootTree, "root");
        var bTree = Tree(("notes.txt", "base one\nline2\nline3\n"));
        var b = Commit(root.Id, rootTree, bTree, "edit the first line");
        var c1Tree = Tree(("notes.txt", "line1\nline2\nchain three\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "edit the last line");
        var mergedText = "base one\nline2\nchain three\n";

        var result = Rebase.Replay(
            rootTree, [c1], b.Id, bTree,
            Lookup("line1\nline2\nline3\n", "base one\nline2\nline3\n", "line1\nline2\nchain three\n"));

        Assert.True(result.IsClean);
        var final = TreeSnapshot.FromChain(bTree, result.Rebased);
        AssertContent(final, "notes.txt", mergedText);

        // The rebased delta references the synthesized blob, and MergedBlobs maps its hash to
        // the text the caller must store.
        var modified = Assert.Single(result.Rebased[0].Changes);
        Assert.Equal(ChangeType.Modified, modified.Type);
        Assert.Equal(HashOf(mergedText), modified.NewBlob);
        Assert.Single(result.MergedBlobs);
        Assert.True(result.MergedBlobs.TryGetValue(HashOf(mergedText), out var stored));
        Assert.Equal(mergedText, stored);
    }

    // ---- Validation ------------------------------------------------------------------------------

    [Fact]
    public void Replay_UnlinkedChain_Throws()
    {
        var rootTree = Tree(("a.txt", "a\n"));
        var root = Root(rootTree, "root");
        var c1Tree = Tree(("a.txt", "b\n"));
        var c1 = Commit(root.Id, rootTree, c1Tree, "c1");
        var otherTree = Tree(("a.txt", "other\n"));
        var other = Root(otherTree, "other root");
        var orphan = Commit(other.Id, otherTree, Tree(("a.txt", "orphan\n")), "orphan");

        Assert.Throws<ArgumentException>(
            () => Rebase.Replay(rootTree, [c1, orphan], root.Id, rootTree, Lookup("a\n", "b\n", "other\n", "orphan\n")));
    }

    [Fact]
    public void Replay_RootStartWithNonEmptyOldBase_Throws()
    {
        var root = Root(Tree(("a.txt", "a\n")), "root");

        Assert.Throws<ArgumentException>(
            () => Rebase.Replay(Tree(("b.txt", "b\n")), [root], root.Id, TreeSnapshot.Empty, Lookup("a\n", "b\n")));
    }

    [Fact]
    public void Replay_EmptyChain_YieldsAnEmptyCleanResult()
    {
        var result = Rebase.Replay(TreeSnapshot.Empty, [], "base", TreeSnapshot.Empty, _ => string.Empty);

        Assert.True(result.IsClean);
        Assert.Empty(result.Rebased);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.MergedBlobs);
    }

    [Fact]
    public void Replay_NullContentLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Rebase.Replay(TreeSnapshot.Empty, [], "base", TreeSnapshot.Empty, null!));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static Changeset Root(TreeSnapshot tree, string message)
    {
        return Changeset.Create([], TreeDiff.Diff(TreeSnapshot.Empty, tree), message, TestAuthor, Timestamp);
    }

    private static Changeset Commit(string parentId, TreeSnapshot parentTree, TreeSnapshot tree, string message)
    {
        return Changeset.Create([parentId], TreeDiff.Diff(parentTree, tree), message, TestAuthor, Timestamp);
    }

    private static Changeset MergeCommit(
        string firstParentId,
        TreeSnapshot firstParentTree,
        string secondParentId,
        TreeSnapshot mergeTree,
        string message)
    {
        return Changeset.Create(
            [firstParentId, secondParentId],
            TreeDiff.Diff(firstParentTree, mergeTree),
            message,
            TestAuthor,
            Timestamp);
    }

    private static string HashOf(string content)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(content)).Hash;
    }

    private static void AssertContent(TreeSnapshot tree, string path, string content)
    {
        Assert.True(tree.TryGet(path, out var entry), $"expected '{path}' in the tree");
        Assert.Equal(HashOf(content), entry!.BlobHash);
    }

    private static void AssertAbsent(TreeSnapshot tree, string path)
    {
        Assert.False(tree.TryGet(path, out _), $"expected '{path}' absent from the tree");
    }

    private static TreeSnapshot Tree(params (string Path, string Content)[] files)
    {
        return TreeSnapshot.Empty.Apply(
            files
                .Select(file => new ChangeEntry(file.Path, null, HashOf(file.Content), FileContentKind.Regular, ChangeType.Added))
                .ToImmutableArray());
    }

    private static Func<string, string> Lookup(params string[] contents)
    {
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in contents)
        {
            byHash[HashOf(content)] = content;
        }

        return hash => byHash[hash];
    }
}
