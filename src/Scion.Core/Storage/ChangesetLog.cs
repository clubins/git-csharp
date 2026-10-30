using Microsoft.Data.Sqlite;

namespace Scion.Core.Storage;

/// <summary>
/// The append-only changeset log: SQLite in write-ahead-log mode is the durable ledger the server
/// appends to and replays at boot. One row per <see cref="Changeset"/>, holding its canonical
/// payload bytes; the row's other columns (id, first parent, timestamp) are denormalized indexes
/// over those bytes, so every stored field is either the payload itself or derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Torn-write safety (spec criterion C6): SQLite's WAL makes each <see cref="AppendRange"/> atomic
/// — a killed process leaves the whole transaction committed or none of it, never a partial
/// changeset — and the next open replays the committed frames and discards the torn tail. That is
/// a process-crash guarantee (<c>kill -9</c>): <c>synchronous=NORMAL</c> commits without an fsync
/// per commit and can lose recent transactions only on OS crash or power loss, which a server
/// process never observes.
/// </para>
/// <para>
/// This class claims only the <c>changesets</c> table and its <c>changesets</c> row in
/// <c>schema_registry</c>. Sibling stores (CAS refs, content-addressed blobs) live beside it as
/// separate classes against the same database file, each owning its own tables and registry row.
/// </para>
/// <para>
/// The log is deliberately dumb about graph semantics: a changeset may reference a parent id that
/// is not in the log yet, and no foreign keys are enforced. Parent rules and DAG invariants are
/// the server layer's concern; this layer guarantees only that every row it returns is a whole,
/// hash-valid changeset.
/// </para>
/// </remarks>
public sealed class ChangesetLog
{
    /// <summary>The <c>schema_registry</c> row this store owns.</summary>
    private const string SchemaName = "changesets";

    private const long SchemaVersion = 1;

    /// <summary>
    /// How long a writer waits for SQLite's write lock before giving up. Appends from parallel
    /// agents serialize on this lock; the bench (spec C5) owns the throughput question, this only
    /// keeps brief contention from surfacing as errors.
    /// </summary>
    private const int BusyTimeoutMilliseconds = 5_000;

    private readonly string _connectionString;

    /// <summary>
    /// Opens (creating on first use) the changeset log inside the database at
    /// <paramref name="databasePath"/>. The constructor is the boot path: it establishes the
    /// schema atomically and validates the registry version, after which SQLite has recovered the
    /// write-ahead log and committed changesets are queryable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database was written by an incompatible schema version.</exception>
    public ChangesetLog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        Initialize();
    }

    /// <summary>Appends one changeset. Idempotent: re-appending an already-stored changeset is a no-op.</summary>
    public void Append(Changeset changeset)
    {
        ArgumentNullException.ThrowIfNull(changeset);
        AppendRange([changeset]);
    }

    /// <summary>
    /// Appends changesets in list order inside a single transaction — either the whole batch
    /// commits or none of it does. Idempotent per entry: an entry whose id is already stored with
    /// identical payload bytes is skipped.
    /// </summary>
    /// <exception cref="ArgumentException">An entry's <see cref="Changeset.Id"/> is not the SHA-256 of its payload.</exception>
    /// <exception cref="InvalidOperationException">An entry's id is already stored with different payload bytes.</exception>
    public void AppendRange(IReadOnlyList<Changeset> changesets)
    {
        ArgumentNullException.ThrowIfNull(changesets);
        if (changesets.Count == 0)
        {
            return;
        }

        // The id/payload invariant is the log's own integrity boundary, checked before any disk
        // touch: the public record constructor can mint a Changeset whose Id does not hash to its
        // payload, and storage is where a lie like that must stop.
        foreach (var changeset in changesets)
        {
            EnsureIdMatchesPayload(changeset);
        }

        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO changesets (id, first_parent, timestamp_utc, canonical)
                VALUES (@id, @firstParent, @timestampUtc, @canonical)
                ON CONFLICT (id) DO NOTHING
                """;
            var id = insert.Parameters.Add("@id", SqliteType.Text);
            var firstParent = insert.Parameters.Add("@firstParent", SqliteType.Text);
            var timestampUtc = insert.Parameters.Add("@timestampUtc", SqliteType.Integer);
            var canonical = insert.Parameters.Add("@canonical", SqliteType.Blob);

            foreach (var changeset in changesets)
            {
                id.Value = changeset.Id;
                firstParent.Value = (object?)changeset.Parents.FirstOrDefault() ?? DBNull.Value;
                timestampUtc.Value = changeset.TimestampUtc;
                canonical.Value = CanonicalChangesetSerializer.Serialize(changeset);

                if (insert.ExecuteNonQuery() == 0)
                {
                    // The id already exists; the only legal existing payload is the byte-identical one.
                    EnsureExistingRowMatches(connection, changeset);
                }
            }

            Execute(connection, "COMMIT");
        }
        catch
        {
            RollbackOnUnwind(connection);
            throw;
        }
    }

    /// <summary>
    /// Replays the log in append order. Every row is hash-validated against its stored id on the
    /// way out — corruption is surfaced loudly, never served as data.
    /// </summary>
    /// <exception cref="InvalidOperationException">A stored row fails validation.</exception>
    public IReadOnlyList<Changeset> Replay()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, canonical FROM changesets ORDER BY seq";
        using var reader = command.ExecuteReader();

        List<Changeset> changesets = [];
        while (reader.Read())
        {
            changesets.Add(ReadValidatedChangeset(reader));
        }

        return changesets;
    }

    /// <summary>Fetches one changeset by id, validating it like <see cref="Replay"/> does.</summary>
    public bool TryGetById(string id, out Changeset? changeset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, canonical FROM changesets WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            changeset = null;
            return false;
        }

        changeset = ReadValidatedChangeset(reader);
        return true;
    }

    /// <summary>True when a changeset with this id is stored.</summary>
    public bool Contains(string id)
    {
        return TryGetById(id, out _);
    }

    /// <summary>The number of changesets in the log.</summary>
    public long Count
    {
        get
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM changesets";
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Full-log integrity audit: re-derives every row's id from its canonical bytes and
    /// cross-checks the denormalized columns. This is O(log) by design — it is an explicit
    /// maintenance action, never part of the boot path (spec: cost scales with the change, not
    /// the repository).
    /// </summary>
    /// <exception cref="InvalidOperationException">A row fails validation.</exception>
    public void Verify()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, id, first_parent, timestamp_utc, canonical FROM changesets ORDER BY seq";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var seq = reader.GetInt64(0);
            var id = reader.GetString(1);
            var storedFirstParent = reader.IsDBNull(2) ? null : reader.GetString(2);
            var storedTimestamp = reader.GetInt64(3);
            var canonical = ReadBlob(reader, 4);

            Changeset changeset;
            try
            {
                changeset = CanonicalChangesetSerializer.Deserialize(canonical);
            }
            catch (InvalidDataException failure)
            {
                throw new InvalidOperationException($"Changeset log row {seq} (id '{id}') does not hold a valid canonical payload: {failure.Message}", failure);
            }

            if (!string.Equals(changeset.Id, id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Changeset log row {seq} stores id '{id}' but its payload hashes to '{changeset.Id}'.");
            }

            var expectedFirstParent = changeset.Parents.Length > 0 ? changeset.Parents[0] : null;
            if (!string.Equals(expectedFirstParent, storedFirstParent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Changeset log row {seq} ('{id}') indexes first parent '{storedFirstParent}' but its payload says '{expectedFirstParent ?? "<none>"}'.");
            }

            if (changeset.TimestampUtc != storedTimestamp)
            {
                throw new InvalidOperationException($"Changeset log row {seq} ('{id}') indexes timestamp {storedTimestamp} but its payload says {changeset.TimestampUtc}.");
            }
        }
    }

    /// <summary>
    /// Checkpoints the write-ahead log into the main database and truncates it to zero length.
    /// Best-effort: returns false when other connections' activity blocks the reset — the WAL
    /// stays consistent either way and a later checkpoint can finish the work.
    /// </summary>
    public bool Checkpoint()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();

        return reader.Read() && reader.GetInt64(0) == 0;
    }

    private void Initialize()
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            Execute(connection, "CREATE TABLE IF NOT EXISTS schema_registry (name TEXT PRIMARY KEY, version INTEGER NOT NULL)");
            Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS changesets (
                    seq INTEGER PRIMARY KEY,
                    id TEXT NOT NULL,
                    first_parent TEXT,
                    timestamp_utc INTEGER NOT NULL,
                    canonical BLOB NOT NULL)
                """);
            Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS changesets_by_id ON changesets(id)");

            Execute(
                connection,
                "INSERT INTO schema_registry (name, version) VALUES (@name, @version) ON CONFLICT (name) DO NOTHING",
                new SqliteParameter("@name", SchemaName),
                new SqliteParameter("@version", SchemaVersion));

            var recorded = (long)Scalar(connection, $"SELECT version FROM schema_registry WHERE name = '{SchemaName}'")!;
            if (recorded != SchemaVersion)
            {
                throw new InvalidOperationException($"Changeset log schema is v{recorded}; this build speaks v{SchemaVersion}.");
            }

            Execute(connection, "COMMIT");
        }
        catch
        {
            RollbackOnUnwind(connection);
            throw;
        }
    }

    /// <summary>
    /// Starts a write transaction with <c>BEGIN IMMEDIATE</c> — the write lock is taken up front,
    /// so parallel writers wait out the busy timeout instead of failing a deferred upgrade with
    /// SQLITE_BUSY_SNAPSHOT. Microsoft.Data.Sqlite's own <see cref="SqliteConnection.BeginTransaction"/>
    /// only issues a deferred BEGIN, hence the raw statement.
    /// </summary>
    private static void BeginImmediate(SqliteConnection connection)
    {
        Execute(connection, "BEGIN IMMEDIATE");
    }

    /// <summary>
    /// Rolls back on the exception path. A ROLLBACK failure here is covered by SQLite itself: the
    /// transaction is left open only because the connection is unusable, and closing it rolls
    /// back any open transaction — so no half-written changeset can survive either way.
    /// </summary>
    private static void RollbackOnUnwind(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>
    /// Opens a connection with the pragmas every operation relies on. WAL mode is persistent in
    /// the database file; the remaining pragmas are per connection.
    /// </summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Busy timeout first, so the WAL-mode assertion below waits out (rather than trips over)
        // a concurrent writer during mode transitions.
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}");
        var journalMode = (string)Scalar(connection, "PRAGMA journal_mode = WAL")!;
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not enable WAL mode on the changeset log (journal mode is '{journalMode}').");
        }

        // NORMAL is the WAL-recommended setting: durable against process death (kill -9), without
        // an fsync per commit. FULL would only add power-loss safety, which a server process does
        // not observe as an error path.
        Execute(connection, "PRAGMA synchronous = NORMAL");
        Execute(connection, "PRAGMA foreign_keys = ON");
        return connection;
    }

    private static void EnsureIdMatchesPayload(Changeset changeset)
    {
        var payloadHash = CanonicalChangesetSerializer.ComputeId(changeset);
        if (!string.Equals(payloadHash, changeset.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Changeset id '{changeset.Id}' is not the SHA-256 of its canonical payload ('{payloadHash}').", nameof(changeset));
        }
    }

    private static void EnsureExistingRowMatches(SqliteConnection connection, Changeset changeset)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT canonical FROM changesets WHERE id = @id";
        query.Parameters.AddWithValue("@id", changeset.Id);
        var stored = query.ExecuteScalar() as byte[]
            ?? throw new InvalidOperationException($"Changeset '{changeset.Id}' was reported present but its row could not be read.");

        if (!stored.AsSpan().SequenceEqual(CanonicalChangesetSerializer.Serialize(changeset)))
        {
            throw new InvalidOperationException($"Changeset '{changeset.Id}' is already stored with different payload bytes; the log is immutable and appends never overwrite.");
        }
    }

    /// <summary>Reads and hash-validates one (id, canonical) row pair at the reader's current position.</summary>
    private static Changeset ReadValidatedChangeset(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var canonical = ReadBlob(reader, 1);

        Changeset changeset;
        try
        {
            changeset = CanonicalChangesetSerializer.Deserialize(canonical);
        }
        catch (InvalidDataException failure)
        {
            throw new InvalidOperationException($"Changeset log row for id '{id}' does not hold a valid canonical payload: {failure.Message}", failure);
        }

        if (!string.Equals(changeset.Id, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Changeset log row for id '{id}' stores a payload that hashes to '{changeset.Id}'.");
        }

        return changeset;
    }

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? throw new InvalidOperationException($"Changeset log row is missing its canonical payload (column {ordinal}).")
            : (byte[])reader.GetValue(ordinal);
    }

    private static void Execute(SqliteConnection connection, string text, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar();
    }
}
