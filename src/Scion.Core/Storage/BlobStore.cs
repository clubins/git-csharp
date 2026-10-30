using System.IO.Compression;

namespace Scion.Core.Storage;

/// <summary>
/// Write-once, content-addressed blob storage on the local filesystem. Blobs are zlib-compressed
/// and stored under a two-level hash shard — <c>root/ab/cd/64-hex</c> — so no directory grows
/// unbounded. A path is immutable once published: writers flush a fully written temp file to disk
/// and move it into place with an atomic no-clobber rename, so concurrent writers of the same
/// content converge on one intact file and no writer can observe another's partial state.
/// </summary>
/// <remarks>
/// Reads decompress and verify the hash before returning, so a corrupted or truncated file fails
/// loudly instead of silently serving wrong bytes. The dedup fast path trusts an already-published
/// path and never rewrites one; repairing externally corrupted files is an operator action, not a
/// store behavior. Instances hold no state beyond <see cref="RootPath"/> and <see cref="Options"/>,
/// and are safe for concurrent use from multiple threads and processes. The store owns exactly its
/// root directory and nothing above it, so the changeset log can live beside it in one repository
/// layout.
/// </remarks>
public sealed class BlobStore
{
    /// <summary>Compression at rest: zlib, git's choice. Optimal trades a little write latency for a materially smaller store.</summary>
    private const CompressionLevel AtRestCompression = CompressionLevel.Optimal;

    /// <summary>Largest decompression chunk, sized to stay off the large-object heap.</summary>
    private const int CopyChunkBytes = 80 * 1024;

    /// <summary>Opens the store rooted at <paramref name="rootPath"/>, creating the directory if absent.</summary>
    public BlobStore(string rootPath, BlobStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        Options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaxBlobBytes, nameof(BlobStoreOptions.MaxBlobBytes));

        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>The directory this store owns; blob files live in its hash-sharded subdirectories.</summary>
    public string RootPath { get; }

    /// <summary>Options governing the store's size caps.</summary>
    public BlobStoreOptions Options { get; }

    /// <summary>
    /// Writes <paramref name="blob"/> under its content hash. Writes of already-stored content are
    /// no-ops (deduplication), and concurrent writes of identical content all succeed against a
    /// single stored file. Returns the normalized (lowercase) hash the blob is addressed by.
    /// </summary>
    /// <exception cref="InvalidOperationException">The blob's hash does not match its content (see <see cref="Blob.EnsureHashMatchesContent"/>).</exception>
    /// <exception cref="BlobTooLargeException">The content exceeds <see cref="BlobStoreOptions.MaxBlobBytes"/>.</exception>
    public string Write(Blob blob)
    {
        var (hash, finalPath) = ValidateForWrite(blob);
        if (File.Exists(finalPath))
        {
            return hash;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = TempPathFor(finalPath, hash);
        try
        {
            WriteCompressed(tempPath, blob.Content.Span);
            Publish(tempPath, finalPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return hash;
    }

    /// <summary>Async twin of <see cref="Write"/>.</summary>
    public async Task<string> WriteAsync(Blob blob, CancellationToken cancellationToken = default)
    {
        var (hash, finalPath) = ValidateForWrite(blob);
        if (File.Exists(finalPath))
        {
            return hash;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = TempPathFor(finalPath, hash);
        try
        {
            await WriteCompressedAsync(tempPath, blob.Content, cancellationToken);
            Publish(tempPath, finalPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return hash;
    }

    /// <summary>
    /// Reads and verifies the blob stored at <paramref name="hash"/>: the decompressed content must
    /// hash back to its address, so corruption is reported instead of served.
    /// </summary>
    /// <exception cref="BlobNotFoundException">No blob is stored at the hash.</exception>
    /// <exception cref="BlobHashMismatchException">The stored bytes are corrupt or undecodable.</exception>
    /// <exception cref="BlobTooLargeException">The stored bytes exceed the size cap, by stat or after decompression.</exception>
    public Blob Read(string hash)
    {
        var (addressed, path) = ValidateForRead(hash);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zlib = new ZLibStream(file, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            return Verified(addressed, ReadUpTo(zlib, Options.MaxBlobBytes));
        }
        catch (InvalidDataException e)
        {
            throw new BlobHashMismatchException(addressed, actualHash: null, e);
        }
    }

    /// <summary>Async twin of <see cref="Read"/>.</summary>
    public async Task<Blob> ReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        var (addressed, path) = ValidateForRead(hash);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await using var zlib = new ZLibStream(file, CompressionMode.Decompress, leaveOpen: true);
            try
            {
                var content = await ReadUpToAsync(zlib, Options.MaxBlobBytes, cancellationToken);
                return Verified(addressed, content);
            }
            catch (InvalidDataException e)
            {
                throw new BlobHashMismatchException(addressed, actualHash: null, e);
            }
        }
        finally
        {
            await file.DisposeAsync();
        }
    }

    /// <summary>True when a blob is stored at <paramref name="hash"/>.</summary>
    /// <exception cref="ArgumentException">The hash is not a 64-character hex SHA-256.</exception>
    public bool Exists(string hash)
    {
        return File.Exists(PathFor(ValidateHash(hash)));
    }

    /// <summary>
    /// Batch missing-blob query for the fetch protocol: the distinct, normalized hashes from
    /// <paramref name="hashes"/> that have no blob on disk, in first-seen order.
    /// </summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        List<string> missing = [];
        HashSet<string> requested = [];
        foreach (var raw in hashes)
        {
            var hash = ValidateHash(raw);
            if (requested.Add(hash) && !File.Exists(PathFor(hash)))
            {
                missing.Add(hash);
            }
        }

        return missing;
    }

    /// <summary>Rejects writes that would break the store's invariant — a path always names exactly the content it is addressed by.</summary>
    private (string Hash, string FinalPath) ValidateForWrite(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        blob.EnsureHashMatchesContent();
        var hash = ValidateHash(blob.Hash);
        if (blob.Content.Length > Options.MaxBlobBytes)
        {
            throw new BlobTooLargeException(blob.Content.Length, Options.MaxBlobBytes);
        }

        return (hash, PathFor(hash));
    }

    /// <summary>Rejects reads of absent or oversize blobs before opening any file.</summary>
    private (string Hash, string Path) ValidateForRead(string hash)
    {
        hash = ValidateHash(hash);
        var path = PathFor(hash);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new BlobNotFoundException(hash);
        }

        if (info.Length > Options.MaxBlobBytes)
        {
            throw new BlobTooLargeException(info.Length, Options.MaxBlobBytes);
        }

        return (hash, path);
    }

    /// <summary>Rejects verified content that does not hash back to the address it was read under.</summary>
    private static Blob Verified(string hash, byte[] content)
    {
        var blob = Blob.Create(content);
        if (!blob.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new BlobHashMismatchException(hash, blob.Hash);
        }

        return blob;
    }

    /// <summary>
    /// Publishes a fully written temp file under its final path with an atomic no-clobber rename.
    /// A loser of the race keeps the winner's identical file; anything else surfaces.
    /// </summary>
    private static void Publish(string tempPath, string finalPath)
    {
        try
        {
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Another writer published this hash first; its file is identical by construction —
            // both renamed fully written, hash-verified content.
        }
    }

    private static void WriteCompressed(string tempPath, ReadOnlySpan<byte> content)
    {
        using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var zlib = new ZLibStream(file, AtRestCompression, leaveOpen: true))
        {
            zlib.Write(content);
        }

        // Fsync before the rename: the file must be complete on disk before its path exists,
        // or a crash could publish a torn blob that no later hash check can repair.
        file.Flush(flushToDisk: true);
    }

    private static async Task WriteCompressedAsync(string tempPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using var zlib = new ZLibStream(file, AtRestCompression, leaveOpen: true);
            await zlib.WriteAsync(content, cancellationToken);

            file.Flush(flushToDisk: true);
        }
        finally
        {
            await file.DisposeAsync();
        }
    }

    /// <summary>Decompresses at most <paramref name="limit"/> bytes; more content than the cap is a size violation, however small the file.</summary>
    private static byte[] ReadUpTo(Stream stream, long limit)
    {
        var output = new MemoryStream();
        var chunk = new byte[CopyChunkBytes];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new BlobTooLargeException(total, limit);
            }

            output.Write(chunk, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> ReadUpToAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var chunk = new byte[CopyChunkBytes];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new BlobTooLargeException(total, limit);
            }

            await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private string PathFor(string hash)
    {
        // Two-level fan-out keeps every directory small at fleet scale.
        return Path.Combine(RootPath, hash[..2], hash[2..4], hash);
    }

    private static string TempPathFor(string finalPath, string hash)
    {
        // Same directory as the final path, so the publishing rename never crosses a filesystem boundary.
        return Path.Combine(Path.GetDirectoryName(finalPath)!, $"{hash}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Validates a blob hash and normalizes it to lowercase. Requiring exactly 64 hex characters
    /// also pins every path component to hex — no input can traverse out of the store root.
    /// </summary>
    private static string ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 64 || !IsHex(hash))
        {
            throw new ArgumentException($"Blob hash '{hash}' is not a 64-character hex SHA-256.", nameof(hash));
        }

        return hash.ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
