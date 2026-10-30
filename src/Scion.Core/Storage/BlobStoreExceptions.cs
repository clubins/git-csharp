namespace Scion.Core.Storage;

/// <summary>
/// Base class for blob store failures, so callers can treat store errors as one family — the API
/// tier maps them to responses, tests assert the specific subtype.
/// </summary>
public abstract class BlobStoreException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A requested blob does not exist in the store.</summary>
public sealed class BlobNotFoundException : BlobStoreException
{
    public BlobNotFoundException(string hash)
        : base($"Blob '{hash}' does not exist in the store.")
    {
        Hash = hash;
    }

    /// <summary>The requested hash, normalized to lowercase.</summary>
    public string Hash { get; }
}

/// <summary>Stored content does not resolve to its address — the signature of a corrupted or truncated blob.</summary>
public sealed class BlobHashMismatchException : BlobStoreException
{
    public BlobHashMismatchException(string hash, string? actualHash, Exception? innerException = null)
        : base(
            actualHash is null
                ? $"Blob '{hash}' is corrupt: its stored bytes could not be decompressed."
                : $"Blob '{hash}' is corrupt: stored content hashes to '{actualHash}', not the addressed hash.",
            innerException)
    {
        Hash = hash;
        ActualHash = actualHash;
    }

    /// <summary>The addressed hash, normalized to lowercase.</summary>
    public string Hash { get; }

    /// <summary>What the stored content actually hashes to, or null when it could not be decompressed at all.</summary>
    public string? ActualHash { get; }
}

/// <summary>A blob exceeds <see cref="BlobStoreOptions.MaxBlobBytes"/> — rejected on write, or encountered on read.</summary>
public sealed class BlobTooLargeException : BlobStoreException
{
    public BlobTooLargeException(long sizeBytes, long maxBytes)
        : base($"Blob is {sizeBytes} bytes; the store accepts at most {maxBytes}.")
    {
        SizeBytes = sizeBytes;
        MaxBytes = maxBytes;
    }

    /// <summary>
    /// The size that was rejected, in bytes. When content decompressed past the cap this is where
    /// the count crossed it — a lower bound, not the full size.
    /// </summary>
    public long SizeBytes { get; }

    /// <summary>The configured cap, in bytes.</summary>
    public long MaxBytes { get; }
}
