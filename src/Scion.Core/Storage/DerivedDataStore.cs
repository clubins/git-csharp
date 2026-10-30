using System.Diagnostics.CodeAnalysis;

using Microsoft.Data.Sqlite;

using Scion.Core.Derived;

namespace Scion.Core.Storage;

/// <summary>
/// Content-addressed persistence for derived data: one row per (derived type, changeset) holding
/// the value's canonical bytes and the SHA-256 those bytes must hash to. Values are written once
/// and never overwritten — a recomputed value is by definition byte-identical (the computation is
/// pure over immutable inputs), so a disagreeing row means corruption and is refused, at write
/// and at read alike.
/// </summary>
/// <remarks>
/// <para>
/// This class claims only the <c>derived_values</c> table and its <c>schema_registry</c> row,
/// beside the changeset log's tables in the same database file — the sibling-store rule
/// <see cref="ChangesetLog"/> documents. A lost or torn row is never a correctness problem (the
/// engine recomputes from the log), so the store needs only to be as durable as the log it sits
/// beside, and it is: same file, same write-ahead log.
/// </para>
/// </remarks>
public sealed class DerivedDataStore
{
    /// <summary>The <c>schema_registry</c> row this store owns.</summary>
    private const string SchemaName = "derived_values";

    private const long SchemaVersion = 1;

    /// <summary>Same contention story as the changeset log: writers wait out brief serialization.</summary>
    private const int BusyTimeoutMilliseconds = 5_000;

    private readonly string _connectionString;

    /// <summary>
    /// Opens (creating on first use) the derived-value store inside the database at
    /// <paramref name="databasePath"/> — conventionally the changeset log's own file.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database was written by an incompatible schema version.</exception>
    public DerivedDataStore(string databasePath)
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

    /// <summary>
    /// Reads one value. The payload is hash-validated on the way out — corruption is surfaced
    /// loudly, never served as data (the log's rule for changeset rows, applied here).
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored payload does not hash to its stored hash.</exception>
    public bool TryGet(string typeName, string changesetId, [NotNullWhen(true)] out DerivedDataValue? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(changesetId);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_hash, payload FROM derived_values WHERE type_name = @type AND changeset_id = @id";
        command.Parameters.AddWithValue("@type", typeName);
        command.Parameters.AddWithValue("@id", changesetId);
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            value = null;
            return false;
        }

        value = ReadValidatedValue(reader, typeName, changesetId);
        return true;
    }

    /// <summary>
    /// Reads the value whose payload hashes to <paramref name="valueHash"/> — the content-addressed
    /// lookup that resolves a tree id (a value's hash is the tree snapshot's id) without knowing
    /// which changeset produced it. The payload is hash-validated on the way out, like
    /// <see cref="TryGet"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored payload does not hash to its stored hash.</exception>
    public bool TryGetByValueHash(string typeName, string valueHash, [NotNullWhen(true)] out DerivedDataValue? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueHash);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_hash, payload FROM derived_values WHERE type_name = @type AND payload_hash = @hash";
        command.Parameters.AddWithValue("@type", typeName);
        command.Parameters.AddWithValue("@hash", valueHash.ToLowerInvariant());
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            value = null;
            return false;
        }

        value = ReadValidatedValue(reader, typeName, valueHash);
        return true;
    }

    /// <summary>
    /// Writes one value. The payload is hash-checked before any disk touch, and the write is
    /// idempotent: a row already present with identical bytes is a no-op, while a row present
    /// with different bytes means two supposedly-pure computations disagreed — corruption, refused.
    /// </summary>
    /// <exception cref="ArgumentException">The value's hash is not the SHA-256 of its payload.</exception>
    /// <exception cref="InvalidOperationException">A different value is already stored under this key.</exception>
    public void Put(string typeName, string changesetId, DerivedDataValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(changesetId);
        ArgumentNullException.ThrowIfNull(value);

        // The id/payload invariant is the store's own integrity boundary, checked before any disk
        // touch — same stance as the changeset log: the record constructor can mint a lie, and
        // storage is where it stops.
        var computedHash = Sha256.HashHex(value.Payload.Span);
        if (!string.Equals(computedHash, value.Hash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Derived value hash '{value.Hash}' is not the SHA-256 of its payload ('{computedHash}').", nameof(value));
        }

        using var connection = Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO derived_values (type_name, changeset_id, payload_hash, payload)
            VALUES (@type, @id, @hash, @payload)
            ON CONFLICT (type_name, changeset_id) DO NOTHING
            """;
        insert.Parameters.AddWithValue("@type", typeName);
        insert.Parameters.AddWithValue("@id", changesetId);
        insert.Parameters.AddWithValue("@hash", value.Hash);
        insert.Parameters.AddWithValue("@payload", value.Payload.ToArray());

        if (insert.ExecuteNonQuery() == 0)
        {
            EnsureExistingRowMatches(connection, typeName, changesetId, value);
        }
    }

    /// <summary>
    /// The changeset ids that have a stored value for one type — the sweep's skip list, without
    /// reading a single payload.
    /// </summary>
    public IReadOnlyList<string> StoredChangesetIds(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT changeset_id FROM derived_values WHERE type_name = @type";
        command.Parameters.AddWithValue("@type", typeName);
        using var reader = command.ExecuteReader();

        List<string> ids = [];
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
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
                CREATE TABLE IF NOT EXISTS derived_values (
                    type_name TEXT NOT NULL,
                    changeset_id TEXT NOT NULL,
                    payload_hash TEXT NOT NULL,
                    payload BLOB NOT NULL,
                    PRIMARY KEY (type_name, changeset_id))
                """);

            Execute(
                connection,
                "INSERT INTO schema_registry (name, version) VALUES (@name, @version) ON CONFLICT (name) DO NOTHING",
                new SqliteParameter("@name", SchemaName),
                new SqliteParameter("@version", SchemaVersion));

            var recorded = (long)Scalar(connection, $"SELECT version FROM schema_registry WHERE name = '{SchemaName}'")!;
            if (recorded != SchemaVersion)
            {
                throw new InvalidOperationException($"Derived-value store schema is v{recorded}; this build speaks v{SchemaVersion}.");
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
    /// Starts a write transaction with <c>BEGIN IMMEDIATE</c> — same reason as the changeset log:
    /// the write lock is taken up front, so parallel writers wait out the busy timeout instead of
    /// failing a deferred upgrade with SQLITE_BUSY_SNAPSHOT.
    /// </summary>
    private static void BeginImmediate(SqliteConnection connection)
    {
        Execute(connection, "BEGIN IMMEDIATE");
    }

    /// <summary>
    /// Rolls back on the exception path. A ROLLBACK failure here is covered by SQLite itself: the
    /// transaction is left open only because the connection is unusable, and closing it rolls
    /// back any open transaction.
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

    /// <summary>Opens a connection with the pragmas every operation relies on; WAL mode is persistent in the file.</summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}");
        var journalMode = (string)Scalar(connection, "PRAGMA journal_mode = WAL")!;
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not enable WAL mode on the derived-value store (journal mode is '{journalMode}').");
        }

        Execute(connection, "PRAGMA synchronous = NORMAL");
        Execute(connection, "PRAGMA foreign_keys = ON");
        return connection;
    }

    private static void EnsureExistingRowMatches(SqliteConnection connection, string typeName, string changesetId, DerivedDataValue value)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT payload_hash, payload FROM derived_values WHERE type_name = @type AND changeset_id = @id";
        query.Parameters.AddWithValue("@type", typeName);
        query.Parameters.AddWithValue("@id", changesetId);
        using var reader = query.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException($"Derived value {typeName}/{changesetId} was reported present but its row could not be read.");
        }

        var existing = ReadValidatedValue(reader, typeName, changesetId);
        if (!string.Equals(existing.Hash, value.Hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Derived value {typeName}/{changesetId} is already stored with different payload bytes; values are immutable and writes never overwrite.");
        }
    }

    /// <summary>Reads and hash-validates one (payload_hash, payload) row pair at the reader's current position.</summary>
    private static DerivedDataValue ReadValidatedValue(SqliteDataReader reader, string typeName, string changesetId)
    {
        var storedHash = reader.IsDBNull(0)
            ? throw new InvalidOperationException($"Derived value row for {typeName}/{changesetId} is missing its payload hash.")
            : reader.GetString(0);
        var payload = reader.IsDBNull(1)
            ? throw new InvalidOperationException($"Derived value row for {typeName}/{changesetId} is missing its payload.")
            : (byte[])reader.GetValue(1);

        var computedHash = Sha256.HashHex(payload);
        if (!string.Equals(computedHash, storedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Derived value for {typeName}/{changesetId} does not hash to its stored hash ('{storedHash}' vs '{computedHash}'); the payload is corrupted.");
        }

        return new DerivedDataValue(storedHash, payload);
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
