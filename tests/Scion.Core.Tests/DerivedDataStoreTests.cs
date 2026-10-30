using System.Text;

using Microsoft.Data.Sqlite;

using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Contract tests for the derived-value store: hash validation at both edges, immutability under
/// re-writes, corruption surfaced on read, and coexistence with the changeset log in one file.
/// </summary>
public class DerivedDataStoreTests
{
    private const string TypeA = "test-type-a";
    private const string TypeB = "test-type-b";
    private const string ChangesetOne = "cs-1";
    private const string ChangesetTwo = "cs-2";

    [Fact]
    public void Put_ThenTryGet_OnFreshInstance_RoundTripsPayload()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        var value = DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("payload-one"));

        store.Put(TypeA, ChangesetOne, value);

        Assert.True(store.TryGet(TypeA, ChangesetOne, out var read));
        Assert.Equal(value, read);
        Assert.Equal(Sha256.HashHex(read!.Payload.Span), read.Hash);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);

        Assert.False(store.TryGet(TypeA, ChangesetOne, out var read));
        Assert.Null(read);
    }

    [Fact]
    public void Put_HashIsNotHashOfPayload_ThrowsBeforeTouchingDisk()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);

        // The same boundary stance as the changeset log: the record constructor can mint a value
        // whose hash does not hash its payload, and storage is where the lie stops.
        var forged = new DerivedDataValue("not-the-hash", Encoding.UTF8.GetBytes("payload-one"));
        Assert.Throws<ArgumentException>(() => store.Put(TypeA, ChangesetOne, forged));
        Assert.False(store.TryGet(TypeA, ChangesetOne, out _));
    }

    [Fact]
    public void Put_DifferentValueUnderSameKey_IsRefused()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        var first = DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("payload-one"));
        var second = DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("payload-two"));

        store.Put(TypeA, ChangesetOne, first);

        Assert.Throws<InvalidOperationException>(() => store.Put(TypeA, ChangesetOne, second));
        Assert.True(store.TryGet(TypeA, ChangesetOne, out var read));
        Assert.Equal(first, read);
    }

    [Fact]
    public void Put_IdenticalValueTwice_IsIdempotent()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        var value = DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("payload-one"));

        store.Put(TypeA, ChangesetOne, value);
        store.Put(TypeA, ChangesetOne, value);

        Assert.Single(store.StoredChangesetIds(TypeA));
    }

    [Fact]
    public void TryGet_CorruptedStoredPayload_ThrowsInsteadOfServing()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        store.Put(TypeA, ChangesetOne, DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("payload-one")));

        Tamper(
            temp.DatabasePath,
            $"UPDATE derived_values SET payload = X'6465616462656566' WHERE type_name = '{TypeA}' AND changeset_id = '{ChangesetOne}'");

        Assert.Throws<InvalidOperationException>(() => store.TryGet(TypeA, ChangesetOne, out _));
    }

    [Fact]
    public void StoredChangesetIds_FiltersByType()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        var value = DerivedDataValue.FromPayload(new byte[] { 1, 2, 3 });
        store.Put(TypeA, ChangesetOne, value);
        store.Put(TypeA, ChangesetTwo, value);
        store.Put(TypeB, ChangesetOne, value);

        Assert.Equal(new[] { ChangesetOne, ChangesetTwo }, store.StoredChangesetIds(TypeA));
        Assert.Equal(new[] { ChangesetOne }, store.StoredChangesetIds(TypeB));
        Assert.Empty(store.StoredChangesetIds("test-type-c"));
    }

    [Fact]
    public void Opens_AlongsideChangesetLog_OnOneDatabaseFile()
    {
        using var temp = new TempDatabase();
        var store = new DerivedDataStore(temp.DatabasePath);
        var log = new ChangesetLog(temp.DatabasePath);
        var root = Root("root", Added("a.cs", BlobA));
        var value = DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes("snapshot-bytes"));

        log.Append(root);
        store.Put(TypeA, root.Id, value);

        // Fresh instances of both stores over the same file: each owns its tables and registry
        // row, neither disturbs the other, and both read back what they wrote.
        var rereadLog = new ChangesetLog(temp.DatabasePath);
        var rereadStore = new DerivedDataStore(temp.DatabasePath);
        Assert.Equal(new[] { root }, rereadLog.Replay());
        Assert.True(rereadStore.TryGet(TypeA, root.Id, out var read));
        Assert.Equal(value, read);
    }

    private static Changeset Root(string message, params ChangeEntry[] changes)
    {
        return Changeset.Create([], [.. changes], message, Author, Timestamp);
    }

    private static ChangeEntry Added(string path, string newBlob)
    {
        return new ChangeEntry(path, null, newBlob, FileContentKind.Regular, ChangeType.Added);
    }

    /// <summary>Runs arbitrary SQL against the database, then drops every pooled handle so later readers cannot serve stale pages.</summary>
    private static void Tamper(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();

        SqliteConnection.ClearAllPools();
    }

    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scion-derived-tests-" + Guid.NewGuid().ToString("N"));
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
