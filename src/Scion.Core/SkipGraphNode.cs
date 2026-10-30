using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// One node of the skip-graph derived over the changeset DAG: the changeset id, its height, and
/// its right-hand pointers — one slot per level below the height. Slot ℓ holds the nearest
/// ancestors (one per parent chain, deduplicated, first parent's chain first) whose height
/// exceeds ℓ; slot 0 is exactly the changeset's parent list.
/// </summary>
/// <remarks>
/// Equality is structural (element-wise over the per-level pointer lists), because default record
/// equality for <see cref="ImmutableArray{T}"/> members compares array references, not elements —
/// two independently built towers for the same DAG would otherwise compare unequal.
/// </remarks>
public sealed record SkipGraphNode(
    string Id,
    int Height,
    ImmutableArray<ImmutableArray<string>> RightPointers)
{
    /// <summary>Compares id, height, and every level's pointers element-wise.</summary>
    public bool Equals(SkipGraphNode? other)
    {
        if (other is null || Id != other.Id || Height != other.Height || RightPointers.Length != other.RightPointers.Length)
        {
            return false;
        }

        for (var level = 0; level < RightPointers.Length; level++)
        {
            if (!RightPointers[level].SequenceEqual(other.RightPointers[level], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hashes the same structure <see cref="Equals"/> compares.</summary>
    public override int GetHashCode()
    {
        var hashCode = new System.HashCode();
        hashCode.Add(Id, StringComparer.Ordinal);
        hashCode.Add(Height);
        foreach (var level in RightPointers)
        {
            foreach (var pointer in level)
            {
                hashCode.Add(pointer, StringComparer.Ordinal);
            }
        }

        return hashCode.ToHashCode();
    }
}
