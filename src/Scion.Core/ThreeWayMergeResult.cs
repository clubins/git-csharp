using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// The outcome of a three-way tree merge (see <see cref="ThreeWayMerge.Merge"/>). The merged
/// tree starts from ours and applies every clean resolution — theirs' changes on paths ours did
/// not touch, and line-level merges of both-side edits; every conflicted path keeps ours' state
/// and is reported in <see cref="Conflicts"/>.
/// </summary>
/// <param name="Tree">The merged tree. Committable as-is when <see cref="IsClean"/>; otherwise
/// conflicted paths hold ours' entries (absent where ours deleted them).</param>
/// <param name="Conflicts">One entry per conflicted path; empty when the merge is clean.</param>
/// <param name="MergedContents">Paths whose content the merge synthesized — clean line-level
/// merges of both-side edits — mapped to the merged text, so the caller can hash and store the
/// new blobs. Content selected wholesale from one side is not synthesized; conflict markers live
/// on <see cref="MergeConflict.MergedContent"/>, not here.</param>
public sealed record ThreeWayMergeResult(
    TreeSnapshot Tree,
    ImmutableArray<MergeConflict> Conflicts,
    ImmutableDictionary<string, string> MergedContents)
{
    /// <summary>True when every path merged cleanly and <see cref="Tree"/> is committable as-is.</summary>
    public bool IsClean => Conflicts.IsEmpty;
}
