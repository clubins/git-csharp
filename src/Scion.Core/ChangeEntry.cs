namespace Scion.Core;

/// <summary>
/// One path's delta relative to the changeset's first parent. <see cref="OldBlob"/> is null when
/// the path was added, <see cref="NewBlob"/> is null when it was deleted, and both are set for a
/// rename — where <see cref="Path"/> is the post-rename path.
/// Blob references are lowercase hex SHA-256 digests (see <see cref="Blob"/>).
/// </summary>
public sealed record ChangeEntry(
    string Path,
    string? OldBlob,
    string? NewBlob,
    FileContentKind Kind,
    ChangeType Type);
