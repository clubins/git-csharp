using Scion.Cli.Local;
using Scion.Cli.Working;
using Scion.Core;

namespace Scion.Cli.Tests;

/// <summary>Unit tests for the shared manifest-vs-working diff: pure, ordered, all four outcomes.</summary>
[Collection("ScionCliSequential")]
public sealed class WorkingChangeSetTests
{
    private static readonly WorkingState.Entry Committed = new("hash-old", FileContentKind.Regular);

    private static WorkingFile File(string path, string hash, FileContentKind kind = FileContentKind.Regular) =>
        new(path, kind, hash, []);

    [Fact]
    public void PathsMissingFromTheManifestAreAdded()
    {
        var changes = WorkingChangeSet.Diff(new Dictionary<string, WorkingState.Entry>(), [File("new.txt", "hash-new")]);

        var change = Assert.Single(changes);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal(ChangeType.Added, change.Type);
        Assert.Null(change.OldHash);
        Assert.Equal("hash-new", change.NewHash);
    }

    [Fact]
    public void PathsWhoseHashDifferedAreModified()
    {
        var changes = WorkingChangeSet.Diff(
            new Dictionary<string, WorkingState.Entry> { ["kept.txt"] = Committed },
            [File("kept.txt", "hash-new")]);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.Modified, change.Type);
        Assert.Equal("hash-old", change.OldHash);
        Assert.Equal("hash-new", change.NewHash);
    }

    [Fact]
    public void ManifestPathsWithoutWorkingFilesAreDeleted()
    {
        var changes = WorkingChangeSet.Diff(
            new Dictionary<string, WorkingState.Entry> { ["gone.txt"] = Committed },
            []);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.Deleted, change.Type);
        Assert.Equal("hash-old", change.OldHash);
        Assert.Null(change.NewHash);
        Assert.Equal(FileContentKind.Regular, change.Kind);
    }

    [Fact]
    public void UnchangedPathsProduceNoChanges()
    {
        var changes = WorkingChangeSet.Diff(
            new Dictionary<string, WorkingState.Entry> { ["same.txt"] = new("hash", FileContentKind.Regular) },
            [File("same.txt", "hash")]);

        Assert.Empty(changes);
    }

    [Fact]
    public void ChangesAreOrderedByOrdinalPath()
    {
        var changes = WorkingChangeSet.Diff(
            new Dictionary<string, WorkingState.Entry>(),
            [File("b.txt", "h"), File("A.txt", "h"), File("a.txt", "h")]);

        Assert.Equal(["A.txt", "a.txt", "b.txt"], changes.Select(change => change.Path).ToArray());
    }

    [Fact]
    public void DeletedChangesKeepTheCommittedKind()
    {
        var changes = WorkingChangeSet.Diff(
            new Dictionary<string, WorkingState.Entry> { ["script.sh"] = new("hash-old", FileContentKind.Executable) },
            []);

        Assert.Equal(FileContentKind.Executable, Assert.Single(changes).Kind);
    }
}
