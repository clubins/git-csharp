using Scion.Core.Derived;

namespace Scion.Core.Tests;

/// <summary>
/// Path-history value semantics through <see cref="PathHistoryValue.Apply"/>: chains accumulate
/// per path, renames thread the source path's chain onto the destination, folding is pure and
/// order-independent, and the canonical encoding round-trips byte-identically.
/// </summary>
public class PathHistoryValueTests
{
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";

    [Fact]
    public void Apply_OnEmpty_BuildsChainsFromNothing()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA), Added("g.cs", BlobB)]);

        Assert.Equal(new[] { "c0" }, root.HistoryOf("f.cs").ToArray());
        Assert.Equal(new[] { "c0" }, root.HistoryOf("g.cs").ToArray());
        Assert.Equal(2, root.Count);
    }

    [Fact]
    public void Apply_Modify_AppendsToTheParentChain_AndLeavesTheParentValueUntouched()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);

        var child = root.Apply("c1", [Modified("f.cs", BlobA, BlobB)]);

        Assert.Equal(new[] { "c0", "c1" }, child.HistoryOf("f.cs").ToArray());
        Assert.Equal(new[] { "c0" }, root.HistoryOf("f.cs").ToArray());
    }

    [Fact]
    public void Apply_DeleteThenReAdd_KeepsOneAccumulatingChain()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);
        var deleted = root.Apply("c1", [Deleted("f.cs", BlobB)]);
        var reAdded = deleted.Apply("c2", [Added("f.cs", BlobC)]);

        Assert.Equal(new[] { "c0", "c1" }, deleted.HistoryOf("f.cs").ToArray());
        Assert.Equal(new[] { "c0", "c1", "c2" }, reAdded.HistoryOf("f.cs").ToArray());
    }

    [Fact]
    public void Apply_Rename_ThreadsTheSourceChainOntoTheDestination()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("src/a.cs", BlobA)]);

        var moved = root.Apply("c1", [Deleted("src/a.cs", BlobA), Renamed("src/b.cs", BlobA)]);

        // The file's history follows the file — and the old path's own chain also ends with the
        // rename, because its removal touched it.
        Assert.Equal(new[] { "c0", "c1" }, moved.HistoryOf("src/b.cs").ToArray());
        Assert.Equal(new[] { "c0", "c1" }, moved.HistoryOf("src/a.cs").ToArray());
    }

    [Fact]
    public void Apply_RenamesInSequence_TheChainFollowsTheFile()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("a.cs", BlobA)]);
        var once = root.Apply("c1", [Deleted("a.cs", BlobA), Renamed("b.cs", BlobA)]);
        var twice = once.Apply("c2", [Deleted("b.cs", BlobA), Renamed("c.cs", BlobA)]);

        Assert.Equal(new[] { "c0", "c1", "c2" }, twice.HistoryOf("c.cs").ToArray());
        Assert.Equal(new[] { "c0", "c1", "c2" }, twice.HistoryOf("b.cs").ToArray());
        Assert.Equal(new[] { "c0", "c1" }, twice.HistoryOf("a.cs").ToArray());
    }

    [Fact]
    public void Apply_UnpairedRenameOntoUnknownPath_StartsTheChainAtTheRenamingChangeset()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);

        var moved = root.Apply("c1", [Renamed("moved.cs", BlobA)]);

        Assert.Equal(new[] { "c1" }, moved.HistoryOf("moved.cs").ToArray());
    }

    [Fact]
    public void Apply_UnpairedRenameOntoKnownPath_ContinuesThatPathsChain()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("b.cs", BlobB)]);

        var moved = root.Apply("c1", [Renamed("b.cs", BlobA)]);

        Assert.Equal(new[] { "c0", "c1" }, moved.HistoryOf("b.cs").ToArray());
    }

    [Fact]
    public void Apply_TwoDeletedPathsWithTheRenamedBlob_PairsTheFirstInOrdinalOrder()
    {
        // x2.cs reaches blob A one changeset before the move, so the two deletion candidates have
        // distinguishable chains — pairing must pick x1.cs (ordinal first) and give y.cs x1's chain.
        var root = PathHistoryValue.Empty.Apply("c0", [Added("x1.cs", BlobA), Added("x2.cs", BlobB)]);
        var edited = root.Apply("c1", [Modified("x2.cs", BlobB, BlobA)]);

        var moved = edited.Apply("c2", [Deleted("x1.cs", BlobA), Deleted("x2.cs", BlobA), Renamed("y.cs", BlobA)]);

        Assert.Equal(new[] { "c0", "c2" }, moved.HistoryOf("y.cs").ToArray());
        Assert.Equal(new[] { "c0", "c1", "c2" }, moved.HistoryOf("x2.cs").ToArray());
    }

    [Fact]
    public void Apply_EmptyDelta_ReturnsTheSameInstance()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);

        Assert.Same(root, root.Apply("c1", []));
    }

    [Fact]
    public void Apply_EntryOrderWithinOneDelta_IsIrrelevant()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);

        // Chains always grow from the pre-apply value, so a duplicate-path delta folds the same
        // way in either order.
        var forward = root.Apply("c1", [Added("g.cs", BlobB), Modified("f.cs", BlobA, BlobC)]);
        var backward = root.Apply("c1", [Modified("f.cs", BlobA, BlobC), Added("g.cs", BlobB)]);

        Assert.Equal(forward, backward);
        Assert.Equal(new[] { "c0", "c1" }, forward.HistoryOf("f.cs").ToArray());
        Assert.Equal(new[] { "c1" }, forward.HistoryOf("g.cs").ToArray());
    }

    [Fact]
    public void Apply_BlankChangesetId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => PathHistoryValue.Empty.Apply(" ", [Added("f.cs", BlobA)]));
    }

    [Fact]
    public void Apply_EntryViolatingTheBlobContract_IsRejected()
    {
        // OldBlob is null exactly when the delta adds the path; NewBlob exactly when it deletes it.
        Assert.Throws<ArgumentException>(
            () => PathHistoryValue.Empty.Apply("c0", [new ChangeEntry("f.cs", BlobA, BlobB, FileContentKind.Regular, ChangeType.Added)]));
        Assert.Throws<ArgumentException>(
            () => PathHistoryValue.Empty.Apply("c0", [new ChangeEntry("f.cs", BlobA, BlobB, FileContentKind.Regular, ChangeType.Deleted)]));
        Assert.Throws<ArgumentException>(
            () => PathHistoryValue.Empty.Apply("c0", [new ChangeEntry("f.cs", BlobA, null, FileContentKind.Regular, ChangeType.Renamed)]));
    }

    [Fact]
    public void Apply_EntryWithBlankPath_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => PathHistoryValue.Empty.Apply("c0", [Added("", BlobA)]));
    }

    [Fact]
    public void HistoryOf_UntouchedPath_ReturnsEmpty_AtEveryValue()
    {
        var root = PathHistoryValue.Empty.Apply("c0", [Added("f.cs", BlobA)]);
        var child = root.Apply("c1", [Modified("f.cs", BlobA, BlobB)]);

        Assert.Empty(PathHistoryValue.Empty.HistoryOf("untouched.cs"));
        Assert.Empty(root.HistoryOf("untouched.cs"));
        Assert.Empty(child.HistoryOf("untouched.cs"));
    }

    [Fact]
    public void HistoryOf_BlankPath_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => PathHistoryValue.Empty.HistoryOf(" "));
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips_ByteIdentically()
    {
        var value = PathHistoryValue.Empty
            .Apply("c0", [Added("b.cs", BlobB), Added("a.cs", BlobA)])
            .Apply("c1", [Modified("a.cs", BlobA, BlobC), Deleted("b.cs", BlobB)]);

        var roundTripped = PathHistoryValue.Deserialize(value.Serialize());

        Assert.Equal(value, roundTripped);
        Assert.Equal(value.Serialize(), roundTripped.Serialize());
    }

    [Fact]
    public void SerializeDeserialize_EmptyValue_RoundTrips()
    {
        var roundTripped = PathHistoryValue.Deserialize(PathHistoryValue.Empty.Serialize());

        Assert.Equal(PathHistoryValue.Empty, roundTripped);
        Assert.Equal(0, roundTripped.Count);
    }

    [Fact]
    public void Deserialize_TruncatedPayload_IsRejected()
    {
        var bytes = PathHistoryValue.Empty
            .Apply("c0", [Added("f.cs", BlobA)])
            .Serialize();

        Assert.Throws<InvalidDataException>(() => PathHistoryValue.Deserialize(bytes[..^1]));
    }

    [Fact]
    public void Deserialize_UnknownVersion_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => PathHistoryValue.Deserialize([0x02, 0, 0, 0, 0]));
    }

    [Fact]
    public void Deserialize_TrailingBytes_AreRejected()
    {
        var bytes = PathHistoryValue.Empty.Serialize();

        Assert.Throws<InvalidDataException>(() => PathHistoryValue.Deserialize([.. bytes, 0x00]));
    }

    [Fact]
    public void Deserialize_EmptyPayload_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => PathHistoryValue.Deserialize([]));
    }

    private static ChangeEntry Added(string path, string newBlob)
    {
        return new ChangeEntry(path, null, newBlob, FileContentKind.Regular, ChangeType.Added);
    }

    private static ChangeEntry Modified(string path, string oldBlob, string newBlob)
    {
        return new ChangeEntry(path, oldBlob, newBlob, FileContentKind.Regular, ChangeType.Modified);
    }

    private static ChangeEntry Deleted(string path, string oldBlob)
    {
        return new ChangeEntry(path, oldBlob, null, FileContentKind.Regular, ChangeType.Deleted);
    }

    private static ChangeEntry Renamed(string newPath, string blob)
    {
        return new ChangeEntry(newPath, blob, blob, FileContentKind.Regular, ChangeType.Renamed);
    }
}
