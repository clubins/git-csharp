using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// One entry in the append-only changeset log. Unlike a git commit, no tree snapshot is stored:
/// <see cref="Changes"/> lists only the paths changed relative to <see cref="Parents"/>[0] (the
/// first parent), and the tree at any changeset is derived later by replaying first-parent
/// changes. Merge changesets therefore carry second-parent content only through
/// <see cref="Parents"/>, never through <see cref="Changes"/>.
/// </summary>
/// <remarks>
/// Equality is structural (element-wise over the parent and change lists, field-wise over the
/// rest), because default record equality for <see cref="ImmutableArray{T}"/> members compares
/// array references, not elements — two independently materialized changesets with the same
/// content would otherwise compare unequal. <see cref="Id"/> is the lowercase hex SHA-256 of the
/// canonical payload encoding (see <see cref="CanonicalChangesetSerializer"/>).
/// </remarks>
public sealed record Changeset(
    string Id,
    ImmutableArray<string> Parents,
    ImmutableArray<ChangeEntry> Changes,
    string Message,
    Author Author,
    long TimestampUtc)
{
    /// <summary>A root has no parents; every other changeset has a first parent and at most one second (a merge).</summary>
    public const int MaxParents = 2;

    /// <summary>
    /// Creates a changeset whose <see cref="Id"/> is the SHA-256 of its canonical payload.
    /// <paramref name="changes"/> must list the paths changed relative to <see cref="Parents"/>[0]
    /// only — deltas against a merge's second parent belong to that parent's own changeset. The
    /// entries are normalized into canonical order (path, ordinal) before hashing.
    /// </summary>
    public static Changeset Create(
        ImmutableArray<string> parents,
        ImmutableArray<ChangeEntry> changes,
        string message,
        Author author,
        long timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(author);
        if (parents.Length > MaxParents)
        {
            throw new ArgumentOutOfRangeException(nameof(parents), parents.Length, $"A changeset has at most {MaxParents} parents.");
        }

        if (parents.IsDefault)
        {
            parents = [];
        }

        var canonicalChanges = CanonicalChangeOrder.Canonicalize(changes);
        var id = CanonicalChangesetSerializer.ComputeId(parents, canonicalChanges, message, author, timestampUtc);
        return new Changeset(id, parents, canonicalChanges, message, author, timestampUtc);
    }

    /// <summary>Compares parents and changes element-wise; the remaining fields field-wise.</summary>
    public bool Equals(Changeset? other)
    {
        return other is not null
            && Id == other.Id
            && Parents.SequenceEqual(other.Parents, StringComparer.Ordinal)
            && Changes.SequenceEqual(other.Changes)
            && Message == other.Message
            && Author == other.Author
            && TimestampUtc == other.TimestampUtc;
    }

    /// <summary>Hashes the same structure <see cref="Equals"/> compares.</summary>
    public override int GetHashCode()
    {
        var hashCode = new System.HashCode();
        hashCode.Add(Id, StringComparer.Ordinal);
        hashCode.Add(Parents.Length);
        foreach (var parent in Parents)
        {
            hashCode.Add(parent, StringComparer.Ordinal);
        }

        hashCode.Add(Changes.Length);
        foreach (var change in Changes)
        {
            hashCode.Add(change);
        }

        hashCode.Add(Message, StringComparer.Ordinal);
        hashCode.Add(Author);
        hashCode.Add(TimestampUtc);
        return hashCode.ToHashCode();
    }
}
