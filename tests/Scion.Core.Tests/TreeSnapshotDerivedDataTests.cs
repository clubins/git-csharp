using Microsoft.Data.Sqlite;

using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Contract tests for the "tree-snapshots" derived type through the engine: the snapshot a stored
/// value decodes to must equal the replayed state, merges build from the first parent only, and
/// the type materializes eagerly as its flag declares.
/// </summary>
public class TreeSnapshotDerivedDataTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string BlobD = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Name_IsTheSpecKey_AndTypeMaterializesEagerly()
    {
        var type = new TreeSnapshotDerivedData();

        Assert.Equal("tree-snapshots", TreeSnapshotDerivedData.TypeName);
        Assert.Equal(TreeSnapshotDerivedData.TypeName, type.Name);
        Assert.True(type.MaterializeEagerly);
    }

    [Fact]
    public void Snapshot_FromDerivedData_EqualsReplayedState()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("src/a.cs", BlobA), Added("docs/readme.md", BlobB));
        var child = ChildOf(root, "child", Modified("src/a.cs", BlobA, BlobC), Deleted("docs/readme.md", BlobB));
        var grandchild = ChildOf(
            child,
            "grandchild",
            Deleted("src/a.cs", BlobC),
            Renamed("src/renamed.cs", BlobC, BlobC),
            Added("src/d.cs", BlobD));
        var chain = new[] { root, child, grandchild };
        new ChangesetLog(db.DatabasePath).AppendRange(chain);
        var engine = Engine(db, new TreeSnapshotDerivedData());

        Assert.Equal(chain.Length, engine.MaterializeEager());

        // Every prefix of the first-parent chain: the stored value decodes to exactly the state
        // replaying that prefix produces — and the value's content hash is the snapshot's id.
        for (var i = 0; i < chain.Length; i++)
        {
            var replayed = TreeSnapshot.FromChain(chain.Take(i + 1));
            var value = engine.Get(TreeSnapshotDerivedData.TypeName, chain[i].Id);

            Assert.Equal(replayed, TreeSnapshotDerivedData.Decode(value));
            Assert.Equal(replayed.Id, value.Hash);
        }
    }

    [Fact]
    public void Compute_Root_BuildsFromTheEmptyTree()
    {
        var type = new TreeSnapshotDerivedData();
        var root = Root("root", Added("src/a.cs", BlobA));

        var value = type.Compute(root, ReadOnlySpan<DerivedDataValue>.Empty);

        Assert.Equal(TreeSnapshot.FromChain([root]), TreeSnapshotDerivedData.Decode(value));
        Assert.Equal(1, TreeSnapshotDerivedData.Decode(value).Count);
    }

    [Fact]
    public void Compute_RootWithoutChanges_YieldsTheEmptyTree()
    {
        var type = new TreeSnapshotDerivedData();
        var root = Root("empty root");

        var value = type.Compute(root, ReadOnlySpan<DerivedDataValue>.Empty);

        Assert.Equal(TreeSnapshot.Empty, TreeSnapshotDerivedData.Decode(value));
        Assert.Equal(TreeSnapshot.Empty.Id, value.Hash);
    }

    [Fact]
    public void Compute_Merge_BuildsFromFirstParent_Only()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("src/a.cs", BlobA), Added("src/base.cs", BlobB));
        var left = ChildOf(root, "left", Modified("src/a.cs", BlobA, BlobC));
        var right = ChildOf(root, "right", Added("src/right.cs", BlobD));
        var merge = Changeset.Create([left.Id, right.Id], [Added("src/merge.cs", BlobC)], "merge", Author, Timestamp);
        new ChangesetLog(db.DatabasePath).AppendRange([root, left, right, merge]);
        var engine = Engine(db, new TreeSnapshotDerivedData());

        var mergeTree = TreeSnapshotDerivedData.Decode(engine.Get(TreeSnapshotDerivedData.TypeName, merge.Id));

        // First parent's state plus the delta; the second parent's own content is not folded in —
        // the data model carries it through Parents, never through Changes.
        Assert.Equal(TreeSnapshot.FromChain([root, left, merge]), mergeTree);
        Assert.True(mergeTree.Entries.ContainsKey("src/merge.cs"));
        Assert.False(mergeTree.Entries.ContainsKey("src/right.cs"));
    }

    [Fact]
    public void Compute_IsPure_SameInputsProduceTheSameValue()
    {
        var type = new TreeSnapshotDerivedData();
        var root = Root("root", Added("src/a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("src/a.cs", BlobA, BlobB));
        var rootValue = type.Compute(root, ReadOnlySpan<DerivedDataValue>.Empty);

        var first = type.Compute(child, [rootValue]);
        var second = type.Compute(child, [rootValue]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Decode_MalformedPayloads_ThrowInvalidData()
    {
        // Wrong version byte.
        Assert.Throws<InvalidDataException>(() => Decode([0x02, 0x00, 0x00, 0x00, 0x00]));
        // Truncated: an entry count with no entries behind it.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x01]));
        // Trailing bytes after a complete encoding of the empty tree.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x00, 0xFF]));
        // Entries out of ordinal path order.
        Assert.Throws<InvalidDataException>(() => Decode(new byte[]
        {
            0x01, 0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x01, (byte)'b', 0x00, 0x00, 0x00, 0x01, (byte)'x', 0x00,
            0x00, 0x00, 0x00, 0x01, (byte)'a', 0x00, 0x00, 0x00, 0x01, (byte)'x', 0x00,
        }));
        // Undefined content kind.
        Assert.Throws<InvalidDataException>(() => Decode(new byte[]
        {
            0x01, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01, (byte)'a', 0x00, 0x00, 0x00, 0x01, (byte)'x', 0x7F,
        }));
    }

    private static TreeSnapshot Decode(byte[] payload)
    {
        return TreeSnapshotDerivedData.Decode(DerivedDataValue.FromPayload(payload));
    }

    /// <summary>An engine over fresh log and store instances on one temporary database.</summary>
    private static DerivedDataEngine Engine(TempDatabase db, params IDerivedData[] types)
    {
        return new DerivedDataEngine(
            new ChangesetLog(db.DatabasePath),
            new DerivedDataStore(db.DatabasePath),
            types);
    }

    private static Changeset Root(string message, params ChangeEntry[] changes)
    {
        return Changeset.Create([], [.. changes], message, Author, Timestamp);
    }

    private static Changeset ChildOf(Changeset parent, string message, params ChangeEntry[] changes)
    {
        return Changeset.Create([parent.Id], [.. changes], message, Author, Timestamp);
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

    private static ChangeEntry Renamed(string path, string oldBlob, string newBlob)
    {
        return new ChangeEntry(path, oldBlob, newBlob, FileContentKind.Regular, ChangeType.Renamed);
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "scion-tree-snapshot-derived-tests-" + Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(directory, "scion.db");
            Directory.CreateDirectory(directory);
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
