using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// Canonical ordering of change entries inside a changeset: by path (ordinal), then type, old
/// blob, new blob, and content kind. Two changesets describing the same delta set in different
/// input orders therefore serialize — and hash — identically.
/// </summary>
internal static class CanonicalChangeOrder
{
    public static readonly Comparison<ChangeEntry> Instance = Compare;

    /// <summary>Returns <paramref name="changes"/> in canonical order, defensively copying first.</summary>
    public static ImmutableArray<ChangeEntry> Canonicalize(ImmutableArray<ChangeEntry> changes)
    {
        if (changes.IsDefault)
        {
            return [];
        }

        var builder = changes.ToBuilder();
        builder.Sort(Instance);
        return builder.ToImmutable();
    }

    private static int Compare(ChangeEntry x, ChangeEntry y)
    {
        var byPath = string.CompareOrdinal(x.Path, y.Path);
        if (byPath != 0)
        {
            return byPath;
        }

        var byType = ((int)x.Type).CompareTo((int)y.Type);
        if (byType != 0)
        {
            return byType;
        }

        var byOldBlob = string.CompareOrdinal(x.OldBlob, y.OldBlob);
        if (byOldBlob != 0)
        {
            return byOldBlob;
        }

        var byNewBlob = string.CompareOrdinal(x.NewBlob, y.NewBlob);
        if (byNewBlob != 0)
        {
            return byNewBlob;
        }

        return ((int)x.Kind).CompareTo((int)y.Kind);
    }
}
