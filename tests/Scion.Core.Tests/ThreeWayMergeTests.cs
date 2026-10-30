using System.Collections.Immutable;
using System.Text;

namespace Scion.Core.Tests;

/// <summary>
/// Unit tests for the three-way merge, line level (<see cref="ThreeWayMerge.MergeText"/>) and
/// tree level (<see cref="ThreeWayMerge.Merge"/>). Tree scenarios are built from path-to-content
/// maps — rename detection inside the merge runs on real <see cref="TreeDiff"/> output, so the
/// tests exercise the same convention production callers get. Each scenario names its three trees
/// and the contents the lookup must resolve.
/// </summary>
public class ThreeWayMergeTests
{
    // ---- MergeText: clean merges ----------------------------------------------------------

    [Fact]
    public void MergeText_IdenticalTexts_IsCleanAndUnchanged()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "a\nb\nc\n", "a\nb\nc\n");

        Assert.True(result.Clean);
        Assert.Equal("a\nb\nc\n", result.Text);
    }

    [Fact]
    public void MergeText_OneSideChanges_TakesThatSide()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "a\nb\nc\n", "a\nB\nc\n");

        Assert.True(result.Clean);
        Assert.Equal("a\nB\nc\n", result.Text);
    }

    [Fact]
    public void MergeText_BothSidesEditDifferentRegions_MergesCleanly()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n");

        Assert.True(result.Clean);
        Assert.Equal("A\nb\nC\n", result.Text);
    }

    [Fact]
    public void MergeText_BothSidesMakeTheSameChange_TakenOnce()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "A\nb\nc\n", "A\nb\nc\n");

        Assert.True(result.Clean);
        Assert.Equal("A\nb\nc\n", result.Text);
    }

    [Fact]
    public void MergeText_BothSidesInsertAtDifferentSpots_MergesCleanly()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "a\nX\nb\nc\n", "a\nb\nc\nY\n");

        Assert.True(result.Clean);
        Assert.Equal("a\nX\nb\nc\nY\n", result.Text);
    }

    // ---- MergeText: conflicts ---------------------------------------------------------------

    [Fact]
    public void MergeText_BothSidesEditTheSameRegion_RendersConflictMarkers()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "A1\nb\nc\n", "A2\nb\nc\n");

        Assert.False(result.Clean);
        Assert.Equal(
            "<<<<<<< ours\nA1\n||||||| base\na\n=======\nA2\n>>>>>>> theirs\nb\nc\n",
            result.Text);
    }

    [Fact]
    public void MergeText_DeleteVersusModify_RendersConflictMarkersWithAnEmptyOursSection()
    {
        var result = ThreeWayMerge.MergeText("a\nb\nc\n", "a\nc\n", "a\nB\nc\n");

        Assert.False(result.Clean);
        Assert.Equal(
            "a\n<<<<<<< ours\n||||||| base\nb\n=======\nB\n>>>>>>> theirs\nc\n",
            result.Text);
    }

    [Fact]
    public void MergeText_CustomLabels_AppearInTheMarkers()
    {
        var result = ThreeWayMerge.MergeText("a\n", "A1\n", "A2\n", "feat/one", "9c5f2a", "feat/two");

        Assert.False(result.Clean);
        Assert.Contains("<<<<<<< feat/one\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("||||||| 9c5f2a\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("=======\n", result.Text, StringComparison.Ordinal);
        Assert.Contains(">>>>>>> feat/two\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeText_EmptyBase_DifferingAdds_Conflict()
    {
        var result = ThreeWayMerge.MergeText(string.Empty, "x\n", "y\n");

        Assert.False(result.Clean);
        Assert.Equal("<<<<<<< ours\nx\n||||||| base\n=======\ny\n>>>>>>> theirs\n", result.Text);
    }

    // ---- Merge: clean tree merges -----------------------------------------------------------

    [Fact]
    public void Merge_ChangesOnDisjointPaths_ComposeCleanly()
    {
        var baseTree = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"));
        var ours = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"), ("ours.txt", "o\n"));
        var theirs = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"), ("theirs.txt", "t\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "b\n", "o\n", "t\n"));

        Assert.True(result.IsClean);
        Assert.Empty(result.MergedContents);
        Assert.Equal(4, result.Tree.Count);
        AssertContent(result.Tree, "ours.txt", "o\n");
        AssertContent(result.Tree, "theirs.txt", "t\n");
        AssertContent(result.Tree, "src/a.txt", "a\n");
        AssertContent(result.Tree, "src/b.txt", "b\n");
    }

    [Fact]
    public void Merge_TheirsEditsWhatOursDidNotTouch_TakesTheirs()
    {
        var baseTree = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"));
        var ours = baseTree;
        var theirs = Tree(("src/a.txt", "A\n"), ("src/b.txt", "b\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "A\n", "b\n"));

        Assert.True(result.IsClean);
        AssertContent(result.Tree, "src/a.txt", "A\n");
        AssertContent(result.Tree, "src/b.txt", "b\n");
    }

    [Fact]
    public void Merge_BothSidesMakeTheSameChange_IsClean()
    {
        var baseTree = Tree(("src/a.txt", "a\n"));
        var ours = Tree(("src/a.txt", "A\n"));
        var theirs = Tree(("src/a.txt", "A\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "A\n"));

        Assert.True(result.IsClean);
        Assert.Empty(result.MergedContents);
        AssertContent(result.Tree, "src/a.txt", "A\n");
    }

    [Fact]
    public void Merge_BothSidesEditOneFileInDifferentRegions_ProducesMergedContent()
    {
        var baseTree = Tree(("src/a.txt", "one\ntwo\nthree\n"));
        var ours = Tree(("src/a.txt", "ONE\ntwo\nthree\n"));
        var theirs = Tree(("src/a.txt", "one\ntwo\nTHREE\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("one\ntwo\nthree\n", "ONE\ntwo\nthree\n", "one\ntwo\nTHREE\n"));

        Assert.True(result.IsClean);
        Assert.Equal("ONE\ntwo\nTHREE\n", result.MergedContents["src/a.txt"]);
        AssertContent(result.Tree, "src/a.txt", "ONE\ntwo\nTHREE\n");
        Assert.NotEqual(HashOf("ONE\ntwo\nthree\n"), EntryAt(result.Tree, "src/a.txt").BlobHash);
    }

    [Fact]
    public void Merge_BothDeleteTheSamePath_IsClean()
    {
        var baseTree = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"));
        var ours = Tree(("src/b.txt", "b\n"));
        var theirs = Tree(("src/b.txt", "b\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "b\n"));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Tree.Count);
        AssertAbsent(result.Tree, "src/a.txt");
    }

    // ---- Merge: content conflicts ----------------------------------------------------------

    [Fact]
    public void Merge_BothSidesEditOneFileInTheSameRegion_ReportsContentConflict()
    {
        var baseTree = Tree(("src/a.txt", "a\nb\nc\n"));
        var ours = Tree(("src/a.txt", "A1\nb\nc\n"));
        var theirs = Tree(("src/a.txt", "A2\nb\nc\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\nb\nc\n", "A1\nb\nc\n", "A2\nb\nc\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/a.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.Content, conflict.Kind);
        Assert.Equal(HashOf("a\nb\nc\n"), conflict.BaseBlob);
        Assert.Equal(HashOf("A1\nb\nc\n"), conflict.OursBlob);
        Assert.Equal(HashOf("A2\nb\nc\n"), conflict.TheirsBlob);
        Assert.Equal(
            "<<<<<<< ours\nA1\n||||||| base\na\n=======\nA2\n>>>>>>> theirs\nb\nc\n",
            conflict.MergedContent);
        Assert.Empty(result.MergedContents);

        // The conflicted path keeps ours' state in the merged tree.
        AssertContent(result.Tree, "src/a.txt", "A1\nb\nc\n");
    }

    [Fact]
    public void Merge_BothAddTheSamePathWithDifferentContent_ReportsContentConflict()
    {
        var baseTree = TreeSnapshot.Empty;
        var ours = Tree(("readme.md", "ours\n"));
        var theirs = Tree(("readme.md", "theirs\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("ours\n", "theirs\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("readme.md", conflict.Path);
        Assert.Equal(MergeConflictKind.Content, conflict.Kind);
        Assert.Null(conflict.BaseBlob);
        Assert.Equal("<<<<<<< ours\nours\n||||||| base\n=======\ntheirs\n>>>>>>> theirs\n", conflict.MergedContent);
        AssertContent(result.Tree, "readme.md", "ours\n");
    }

    // ---- Merge: delete/modify conflicts -------------------------------------------------

    [Fact]
    public void Merge_OursDeletesWhatTheirsModifies_ReportsDeleteModify()
    {
        var baseTree = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"));
        var ours = Tree(("src/b.txt", "b\n"));
        var theirs = Tree(("src/a.txt", "A\n"), ("src/b.txt", "b\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "A\n", "b\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/a.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.DeleteModify, conflict.Kind);
        Assert.Equal(HashOf("a\n"), conflict.BaseBlob);
        Assert.Null(conflict.OursBlob);
        Assert.Equal(HashOf("A\n"), conflict.TheirsBlob);

        // Ours' deletion stands.
        Assert.Equal(1, result.Tree.Count);
        AssertAbsent(result.Tree, "src/a.txt");
    }

    [Fact]
    public void Merge_OursModifiesWhatTheirsDeletes_ReportsDeleteModify()
    {
        var baseTree = Tree(("src/a.txt", "a\n"), ("src/b.txt", "b\n"));
        var ours = Tree(("src/a.txt", "A\n"), ("src/b.txt", "b\n"));
        var theirs = Tree(("src/b.txt", "b\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("a\n", "A\n", "b\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/a.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.DeleteModify, conflict.Kind);
        Assert.Equal(HashOf("A\n"), conflict.OursBlob);
        Assert.Null(conflict.TheirsBlob);

        // Ours' version stands.
        Assert.Equal(2, result.Tree.Count);
        AssertContent(result.Tree, "src/a.txt", "A\n");
    }

    // ---- Merge: renames -------------------------------------------------------------------

    [Fact]
    public void Merge_OursRenamesWhatTheirsNeverTouched_RenameApplied()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/new.txt", "v1\n"));
        var theirs = baseTree;

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n"));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/new.txt", "v1\n");
        AssertAbsent(result.Tree, "src/old.txt");
    }

    [Fact]
    public void Merge_BothRenameTheSameWay_IsClean()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/new.txt", "v1\n"));
        var theirs = Tree(("src/new.txt", "v1\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n"));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/new.txt", "v1\n");
        AssertAbsent(result.Tree, "src/old.txt");
    }

    [Fact]
    public void Merge_OursRenamesWhileTheirsEditsContent_EditsLandOnTheNewPath()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/new.txt", "v1\n"));
        var theirs = Tree(("src/old.txt", "v2\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n", "v2\n"));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/new.txt", "v2\n");
        AssertAbsent(result.Tree, "src/old.txt");
    }

    [Fact]
    public void Merge_TheirsRenamesWhileOursEditsContent_OursEditsSurviveTheRename()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/old.txt", "v2\n"));
        var theirs = Tree(("src/new.txt", "v1\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n", "v2\n"));

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/new.txt", "v2\n");
        AssertAbsent(result.Tree, "src/old.txt");
    }

    [Fact]
    public void Merge_BothRenameToDifferentPaths_ReportsRenameRename()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/ours.txt", "v1\n"));
        var theirs = Tree(("src/theirs.txt", "v1\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/old.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.RenameRename, conflict.Kind);
        Assert.Equal(HashOf("v1\n"), conflict.BaseBlob);
        Assert.Equal("src/ours.txt", conflict.OursTarget);
        Assert.Equal("src/theirs.txt", conflict.TheirsTarget);

        // Ours' rename stands.
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/ours.txt", "v1\n");
        AssertAbsent(result.Tree, "src/theirs.txt");
    }

    [Fact]
    public void Merge_OursRenamesWhatTheirsDeletes_ReportsRenameDelete()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = Tree(("src/new.txt", "v1\n"));
        var theirs = TreeSnapshot.Empty;

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/old.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.RenameDelete, conflict.Kind);
        Assert.Equal("src/new.txt", conflict.OursTarget);
        Assert.Null(conflict.TheirsTarget);

        // Ours' rename stands.
        Assert.Equal(1, result.Tree.Count);
        AssertContent(result.Tree, "src/new.txt", "v1\n");
    }

    [Fact]
    public void Merge_OursDeletesWhatTheirsRenames_ReportsRenameDelete()
    {
        var baseTree = Tree(("src/old.txt", "v1\n"));
        var ours = TreeSnapshot.Empty;
        var theirs = Tree(("src/new.txt", "v1\n"));

        var result = ThreeWayMerge.Merge(baseTree, ours, theirs, Lookup("v1\n"));

        Assert.False(result.IsClean);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/old.txt", conflict.Path);
        Assert.Equal(MergeConflictKind.RenameDelete, conflict.Kind);
        Assert.Null(conflict.OursTarget);
        Assert.Equal("src/new.txt", conflict.TheirsTarget);

        // Ours' deletion stands.
        Assert.Equal(0, result.Tree.Count);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static string HashOf(string content)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(content)).Hash;
    }

    private static TreeEntry EntryAt(TreeSnapshot tree, string path)
    {
        Assert.True(tree.TryGet(path, out var entry), $"expected '{path}' in the tree");
        return entry!;
    }

    private static void AssertContent(TreeSnapshot tree, string path, string content)
    {
        Assert.True(tree.TryGet(path, out var entry), $"expected '{path}' in the merged tree");
        Assert.Equal(HashOf(content), entry!.BlobHash);
    }

    private static void AssertAbsent(TreeSnapshot tree, string path)
    {
        Assert.False(tree.TryGet(path, out _), $"expected '{path}' absent from the merged tree");
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
