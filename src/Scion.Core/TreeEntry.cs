namespace Scion.Core;

/// <summary>
/// One path's entry in a <see cref="TreeSnapshot"/>: the lowercase hex SHA-256 of the path's
/// content (see <see cref="Blob"/>) and how the working-copy materializer must interpret it.
/// </summary>
/// <remarks>
/// Hashes compare case-insensitively, matching <see cref="Blob"/> equality — both spellings name
/// the same content-addressed blob.
/// </remarks>
public sealed record TreeEntry
{
    public TreeEntry(string blobHash, FileContentKind kind)
    {
        if (string.IsNullOrWhiteSpace(blobHash))
        {
            throw new ArgumentException("A tree entry requires a blob hash.", nameof(blobHash));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Unknown {nameof(FileContentKind)} value {kind}.", nameof(kind));
        }

        BlobHash = blobHash;
        Kind = kind;
    }

    public string BlobHash { get; }

    public FileContentKind Kind { get; }

    public void Deconstruct(out string blobHash, out FileContentKind kind)
    {
        blobHash = BlobHash;
        kind = Kind;
    }

    /// <summary>Compares hashes case-insensitively and kinds exactly.</summary>
    public bool Equals(TreeEntry? other)
    {
        return other is not null
            && string.Equals(BlobHash, other.BlobHash, StringComparison.OrdinalIgnoreCase)
            && Kind == other.Kind;
    }

    /// <summary>Hashes by identity only: the hash string already names the content.</summary>
    public override int GetHashCode()
    {
        return System.HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(BlobHash), Kind);
    }
}
