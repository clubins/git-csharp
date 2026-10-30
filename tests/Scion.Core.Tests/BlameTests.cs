using System.Collections.Immutable;

namespace Scion.Core.Tests;

/// <summary>
/// Blame attribution over version chains: every line of the newest version carries the changeset
/// that introduced it, unchanged lines keep their attribution across versions, and an empty
/// middle version — the deleted-then-re-added path — re-attributes everything to the re-add.
/// </summary>
public class BlameTests
{
    [Fact]
    public void Annotate_SingleVersion_AttributesEveryLineToIt()
    {
        var lines = Blame.Annotate([V("c1", "alpha\nbeta\n")]);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("c1", line.ChangesetId));
        Assert.Equal(1, lines[0].Line);
        Assert.Equal("alpha\n", lines[0].Content);
        Assert.Equal(2, lines[1].Line);
        Assert.Equal("beta\n", lines[1].Content);
    }

    [Fact]
    public void Annotate_ModifiedLine_AttributesToTheVersionThatRewroteIt()
    {
        var lines = Blame.Annotate([
            V("c1", "alpha\nbeta\ngamma\n"),
            V("c2", "alpha\nBETA\ngamma\n"),
        ]);

        Assert.Equal(new[] { "c1", "c2", "c1" }, IdsOf(lines));
        Assert.Equal("BETA\n", lines[1].Content);
    }

    [Fact]
    public void Annotate_AppendedLines_AttributeToTheVersionThatAddedThem()
    {
        var lines = Blame.Annotate([
            V("c1", "one\ntwo\n"),
            V("c2", "one\ntwo\nthree\nfour\n"),
        ]);

        Assert.Equal(new[] { "c1", "c1", "c2", "c2" }, IdsOf(lines));
        Assert.Equal(4, lines[3].Line);
        Assert.Equal("four\n", lines[3].Content);
    }

    [Fact]
    public void Annotate_DeletedThenReaddedPath_AttributesEverythingToTheReAdd()
    {
        var lines = Blame.Annotate([
            V("c1", "x\ny\n"),
            V("c2", string.Empty), // the path is gone at c2 — its deletion is the empty version
            V("c3", "x\ny\n"),
        ]);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("c3", line.ChangesetId));
    }

    [Fact]
    public void Annotate_RemovedLines_LoseTheirAttributionWithoutDisturbingTheRest()
    {
        var lines = Blame.Annotate([
            V("c1", "keep\nlose\nkeep too\n"),
            V("c2", "keep\nkeep too\n"),
        ]);

        Assert.Equal(new[] { "c1", "c1" }, IdsOf(lines));
        Assert.Equal(new[] { 1, 2 }, lines.Select(line => line.Line).ToArray());
        Assert.Equal("keep too\n", lines[1].Content);
    }

    [Fact]
    public void Annotate_AttributionSurvivesAnUntouchedVersion()
    {
        // c2 rewrites the file; c3 leaves it alone. Lines keep the attribution they earned at c2.
        var lines = Blame.Annotate([
            V("c1", "a\n"),
            V("c2", "b\nc\n"),
            V("c3", "b\nc\n"),
        ]);

        Assert.Equal(new[] { "c2", "c2" }, IdsOf(lines));
    }

    [Fact]
    public void Annotate_EmptyNewestVersion_IsAnEmptyResult()
    {
        var lines = Blame.Annotate([V("c1", "gone\n"), V("c2", string.Empty)]);

        Assert.Empty(lines);
    }

    [Fact]
    public void Annotate_MissingFinalNewline_KeepsTheLastLineExact()
    {
        var lines = Blame.Annotate([V("c1", "a\nb")]);

        Assert.Equal(new[] { "a\n", "b" }, lines.Select(line => line.Content).ToArray());
        Assert.All(lines, line => Assert.Equal("c1", line.ChangesetId));
    }

    [Fact]
    public void Annotate_EmptyFirstVersion_AttributesEverythingToTheFirstNonEmptyVersion()
    {
        var lines = Blame.Annotate([V("c1", string.Empty), V("c2", "hello\n")]);

        var line = Assert.Single(lines);
        Assert.Equal("c2", line.ChangesetId);
        Assert.Equal("hello\n", line.Content);
    }

    [Fact]
    public void Annotate_RejectsAnEmptyVersionList()
    {
        Assert.Throws<ArgumentException>(() => Blame.Annotate([]));
        Assert.Throws<ArgumentException>(() => Blame.Annotate(default));
    }

    [Fact]
    public void Annotate_RejectsAVersionWithNoChangesetId()
    {
        Assert.Throws<ArgumentException>(() => Blame.Annotate([new Blame.BlameVersion(" ", "a\n")]));
        Assert.Throws<ArgumentException>(() => Blame.Annotate([V("c1", "a\n"), new Blame.BlameVersion("", "b\n")]));
    }

    // --- PreviousName ---

    [Fact]
    public void PreviousName_ModifiedOrDeleted_KeepsTheName()
    {
        var delta = new[]
        {
            E("docs/app.cs", "h1", "h2", ChangeType.Modified),
            E("docs/old.md", "h3", null, ChangeType.Deleted),
        }.ToImmutableArray();

        Assert.Equal("docs/app.cs", Blame.PreviousName(delta, "docs/app.cs"));
        Assert.Equal("docs/old.md", Blame.PreviousName(delta, "docs/old.md"));
    }

    [Fact]
    public void PreviousName_ReaddedPath_KeepsTheNameForThePathLineage()
    {
        // A re-add continues the same path's chain: the deleted state is a missing tree entry,
        // not a name change — blame versions it as empty content and re-attributes from there.
        var delta = new[] { E("docs/notes.md", null, "h1", ChangeType.Added) }.ToImmutableArray();

        Assert.Equal("docs/notes.md", Blame.PreviousName(delta, "docs/notes.md"));
    }

    [Fact]
    public void PreviousName_Renamed_RecoversThePairedDeletionPath()
    {
        var delta = new[]
        {
            E("docs/a.md", "h1", null, ChangeType.Deleted),
            E("docs/b.md", "h1", "h1", ChangeType.Renamed),
        }.ToImmutableArray();

        Assert.Equal("docs/a.md", Blame.PreviousName(delta, "docs/b.md"));
    }

    [Fact]
    public void PreviousName_DeltaNotTouchingTheName_KeepsTheName()
    {
        var delta = new[] { E("other.txt", null, "h1", ChangeType.Added) }.ToImmutableArray();

        Assert.Equal("docs/app.cs", Blame.PreviousName(delta, "docs/app.cs"));
    }

    [Fact]
    public void PreviousName_BlankName_Throws()
    {
        Assert.Throws<ArgumentException>(() => Blame.PreviousName([], " "));
    }

    private static ChangeEntry E(string path, string? oldBlob, string? newBlob, ChangeType type)
    {
        return new ChangeEntry(path, oldBlob, newBlob, FileContentKind.Regular, type);
    }

    private static Blame.BlameVersion V(string changesetId, string content)
    {
        return new Blame.BlameVersion(changesetId, content);
    }

    private static string[] IdsOf(ImmutableArray<Blame.LineAttribution> lines)
    {
        return lines.Select(line => line.ChangesetId).ToArray();
    }
}
