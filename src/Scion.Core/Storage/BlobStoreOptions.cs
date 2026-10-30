namespace Scion.Core.Storage;

/// <summary>Tuning knobs for <see cref="BlobStore"/>.</summary>
public sealed record BlobStoreOptions
{
    /// <summary>
    /// Default per-blob cap: 100 MiB — comfortably above any source file, low enough to keep a bad
    /// upload from exhausting memory.
    /// </summary>
    public const long DefaultMaxBlobBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum accepted blob size in bytes. Enforced before a write touches disk and again on every
    /// read — including after decompression, so a compressed-size bomb cannot expand past the cap.
    /// </summary>
    public long MaxBlobBytes { get; init; } = DefaultMaxBlobBytes;
}
