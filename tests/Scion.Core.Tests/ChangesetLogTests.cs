using System.Text;
using Microsoft.Data.Sqlite;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Contract tests for the changeset log, including the spec C6 fault injection: a raw connection
/// with a deliberately tiny page cache plays the server killed mid-append, the database files are
/// captured at that instant and restored byte-for-byte, and the reopened log must show a
/// consistent, hash-valid history with no partial changeset visible.
/// </summary>
public class ChangesetLogTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string BlobD = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";
    private const string BlobE = "0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Replay_OnFreshLog_IsEmpty()
    {
        using var temp = new TempDatabase();

        var log = new ChangesetLog(temp.DatabasePath);

        Assert.Empty(log.Replay());
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void AppendRange_ThenReplayOnFreshInstance_RoundTripsExactly()
    {
        using var temp = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA), Modified("b.cs", BlobB, BlobC));
        var child = ChildOf(root, "child", Deleted("a.cs", BlobA));
        var merge = Changeset.Create([child.Id, root.Id], [Modified("c.cs", BlobD, BlobE)], "merge", Author, Timestamp);
        var expected = new[] { root, child, merge };

        var writer = new ChangesetLog(temp.DatabasePath);
        writer.AppendRange(expected);

        var reader = new ChangesetLog(temp.DatabasePath);
        Assert.Equal(expected, reader.Replay());
        Assert.Equal(expected.Length, reader.Count);
        reader.Verify();
    }

    [Fact]
    public void Append_DuplicateIdWithIdenticalPayload_IsIdempotent()
    {
        using var temp = new TempDatabase();
        var changeset = Root("root", Added("a.cs", BlobA));
        var log = new ChangesetLog(temp.DatabasePath);

        log.Append(changeset);
        log.Append(changeset);

        Assert.Equal(1, log.Count);
        Assert.Equal(new[] { changeset }, log.Replay());
    }

    [Fact]
    public void Append_IdIsNotHashOfPayload_ThrowsBeforeTouchingDisk()
    {
        using var temp = new TempDatabase();
        var stored = Root("stored", Added("a.cs", BlobA));
        var log = new ChangesetLog(temp.DatabasePath);
        log.Append(stored);

        // A hand-minted record whose Id is not the hash of its payload — here the id of an
        // existing changeset over different bytes — is rejected at the boundary, before any disk
        // write, and the stored log is untouched.
        var forged = new Changeset(stored.Id, stored.Parents, stored.Changes, "different message", Author, Timestamp);
        Assert.Throws<ArgumentException>(() => log.Append(forged));

        Assert.Equal(1, log.Count);
        Assert.Equal(new[] { stored }, log.Replay());
    }

    [Fact]
    public void Append_UnknownParent_IsAccepted()
    {
        // The log is a dumb append-only ledger; DAG rules (a parent must exist, be an ancestor of
        // the trunk, etc.) are the server layer's concern. This pins that boundary.
        using var temp = new TempDatabase();
        var orphan = Changeset.Create(
            [new string('f', 64)],
            [Added("a.cs", BlobA)],
            "orphan",
            Author,
            Timestamp);
        var log = new ChangesetLog(temp.DatabasePath);

        log.Append(orphan);

        Assert.Equal(new[] { orphan }, log.Replay());
    }

    [Fact]
    public void TryGetById_MissingId_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var log = new ChangesetLog(temp.DatabasePath);

        Assert.False(log.TryGetById(new string('0', 64), out var changeset));
        Assert.Null(changeset);
        Assert.False(log.Contains(new string('0', 64)));
    }

    [Fact]
    public void TryGetById_StoredId_ReturnsValidatedChangeset()
    {
        using var temp = new TempDatabase();
        var stored = Root("stored", Added("a.cs", BlobA));
        var log = new ChangesetLog(temp.DatabasePath);
        log.Append(stored);

        Assert.True(log.TryGetById(stored.Id, out var fetched));
        Assert.Equal(stored, fetched);
    }

    [Fact]
    public void Boot_AfterInjectedKillMidAppend_RecoversWithNoPartialChangesetVisible()
    {
        using var temp = new TempDatabase();
        var path = temp.DatabasePath;
        var first = Root("first", Added("a.cs", BlobA));
        var second = ChildOf(first, "second", Modified("b.cs", BlobB, BlobC));
        var committed = new[] { first, second };
        new ChangesetLog(path).AppendRange(committed);

        // Crash injection: a raw connection with a 32 KiB page cache plays the server killed
        // mid-append. Its uncommitted transaction writes a complete changeset row plus enough
        // filler to spill dirty pages into the WAL, the files are captured at that instant, and
        // the process "dies" — restoring the captured image is byte-identical to what kill -9
        // leaves on disk: committed frames plus an uncommitted tail without a commit record.
        var inFlight = ChildOf(second, "in-flight", Added("z.cs", BlobE));
        byte[] databaseAtCrash;
        byte[] walAtCrash;
        using (var dying = new SqliteConnection($"Data Source={path}"))
        {
            dying.Open();
            ExecuteRaw(dying, "PRAGMA cache_size = -32");
            ExecuteRaw(dying, "BEGIN IMMEDIATE");
            try
            {
                InsertRawRow(dying, (inFlight.Id, CanonicalChangesetSerializer.Serialize(inFlight)));
                for (var i = 0; i < 200; i++)
                {
                    InsertRawRow(dying, GarbageRow(i));
                }

                var walFile = path + "-wal";
                Assert.True(File.Exists(walFile), "the WAL should exist while the mid-append transaction is open");
                walAtCrash = File.ReadAllBytes(walFile);
                databaseAtCrash = File.ReadAllBytes(path);
            }
            finally
            {
                ExecuteRaw(dying, "ROLLBACK");
            }
        }

        // The uncommitted changeset's bytes really reached the WAL before the crash — the recovery
        // below has something torn to discard.
        Assert.True(ContainsBytes(walAtCrash, Encoding.ASCII.GetBytes(inFlight.Id)), "the in-flight row should have spilled into the WAL");

        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(path, databaseAtCrash);
        File.WriteAllBytes(path + "-wal", walAtCrash);
        File.Delete(path + "-shm"); // the shared-memory index dies with the process; recovery rebuilds it

        var recovered = new ChangesetLog(path);
        Assert.Equal(committed, recovered.Replay());
        Assert.Equal(committed.Length, recovered.Count);
        Assert.False(recovered.Contains(inFlight.Id), "an uncommitted changeset must not survive the crash");

        recovered.Append(ChildOf(second, "after restart", Added("post.cs", BlobD)));
        Assert.Equal(committed.Length + 1, recovered.Count);
        recovered.Verify();
    }

    [Fact]
    public void Checkpoint_MovesCommittedFramesIntoDatabase_TruncatesWal()
    {
        using var temp = new TempDatabase();
        var log = new ChangesetLog(temp.DatabasePath);
        log.AppendRange(new[] { Root("first", Added("a.cs", BlobA)), Root("second", Added("b.cs", BlobB)) });
        var walFile = temp.DatabasePath + "-wal";

        Assert.True(new FileInfo(walFile).Length > 0, "committed frames should sit in the WAL before a checkpoint");

        Assert.True(log.Checkpoint());
        Assert.Equal(0, new FileInfo(walFile).Length);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Verify_TamperedPayload_Throws()
    {
        using var temp = new TempDatabase();
        var stored = Root("stored", Added("a.cs", BlobA));
        var log = new ChangesetLog(temp.DatabasePath);
        log.Append(stored);
        Tamper(temp.DatabasePath, $"UPDATE changesets SET canonical = zeroblob(8) WHERE id = '{stored.Id}'");

        var reopened = new ChangesetLog(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(reopened.Verify);
        Assert.Throws<InvalidOperationException>(reopened.Replay);
    }

    [Fact]
    public void Verify_TamperedIndexColumn_ThrowsWhileReplayServesCanonicalBytes()
    {
        using var temp = new TempDatabase();
        var stored = ChildOf(Root("root", Added("a.cs", BlobA)), "child", Added("b.cs", BlobB));
        var log = new ChangesetLog(temp.DatabasePath);
        log.Append(stored);
        Tamper(temp.DatabasePath, "UPDATE changesets SET first_parent = '" + new string('f', 64) + "'");

        var reopened = new ChangesetLog(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(reopened.Verify);
        // The canonical bytes are the source of truth: the denormalized column is an index, so
        // replay still serves the exact stored changeset.
        Assert.Equal(new[] { stored }, reopened.Replay());
    }

    [Fact]
    public void Open_UnknownSchemaVersion_Throws()
    {
        using var temp = new TempDatabase();
        var log = new ChangesetLog(temp.DatabasePath);
        log.Append(Root("stored", Added("a.cs", BlobA)));
        Tamper(temp.DatabasePath, "UPDATE schema_registry SET version = version + 1 WHERE name = 'changesets'");

        Assert.Throws<InvalidOperationException>(() => new ChangesetLog(temp.DatabasePath));
    }

    [Fact]
    public void Open_SequentialInstances_DataPersistsAcrossBoots()
    {
        using var temp = new TempDatabase();
        var first = Root("first", Added("a.cs", BlobA));
        var second = ChildOf(first, "second", Added("b.cs", BlobB));

        new ChangesetLog(temp.DatabasePath).Append(first);
        new ChangesetLog(temp.DatabasePath).Append(second);

        Assert.Equal(new[] { first, second }, new ChangesetLog(temp.DatabasePath).Replay());
    }

    [Fact]
    public async Task Append_ParallelWriters_SerializeWithoutLoss()
    {
        using var temp = new TempDatabase();
        var writerA = new ChangesetLog(temp.DatabasePath);
        var writerB = new ChangesetLog(temp.DatabasePath);
        var batch = Enumerable.Range(0, 50).Select(i => Root($"changeset-{i}")).ToArray();

        await Task.WhenAll(
            Task.Run(() => { foreach (var changeset in batch.Take(25)) { writerA.Append(changeset); } }),
            Task.Run(() => { foreach (var changeset in batch.Skip(25)) { writerB.Append(changeset); } }));

        var reader = new ChangesetLog(temp.DatabasePath);
        Assert.Equal(50, reader.Count);
        Assert.Equal(50, reader.Replay().Select(cs => cs.Id).Distinct().Count());
        reader.Verify();
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

    /// <summary>A large junk row with a unique id, used to force the page cache to spill into the WAL.</summary>
    private static (string Id, byte[] Canonical) GarbageRow(int index)
    {
        var payload = new byte[2048];
        new Random(index).NextBytes(payload);
        return ($"garbage-{index:D6}", payload);
    }

    private static void InsertRawRow(SqliteConnection connection, (string Id, byte[] Canonical) row)
    {
        ExecuteRaw(connection, $"""
            INSERT INTO changesets (id, first_parent, timestamp_utc, canonical)
            VALUES ('{row.Id}', NULL, 0, @canonical)
            """, new SqliteParameter("@canonical", row.Canonical));
    }

    private static void ExecuteRaw(SqliteConnection connection, string text, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>Runs arbitrary SQL against the database, then drops every pooled handle so later readers cannot serve stale pages.</summary>
    private static void Tamper(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        ExecuteRaw(connection, sql);

        SqliteConnection.ClearAllPools();
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A temp directory plus the database path inside it; disposal drops pooled handles first so the tree can be deleted.</summary>
    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scion-log-tests-" + Guid.NewGuid().ToString("N"));
            DatabasePath = System.IO.Path.Combine(directory, "scion.db");
            System.IO.Directory.CreateDirectory(directory);
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
