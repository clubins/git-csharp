namespace Scion.Core;

/// <summary>
/// One path a three-way merge could not reconcile (see <see cref="ThreeWayMerge.Merge"/>). The
/// merged tree keeps ours' state for the conflicted path — resolution replaces it.
/// </summary>
/// <param name="Path">The conflicted path: the file path for content and delete/modify conflicts,
/// the rename source for rename conflicts.</param>
/// <param name="Kind">Why the path conflicted.</param>
/// <param name="BaseBlob">The blob the path (or, for rename conflicts, the moved content) had at
/// the merge base; null when it did not exist there.</param>
/// <param name="OursBlob">The path's blob on ours' side; null when ours does not have it.</param>
/// <param name="TheirsBlob">The path's blob on theirs' side; null when theirs does not have it.</param>
/// <param name="MergedContent">For <see cref="MergeConflictKind.Content"/> conflicts: the merged
/// text with conflict markers. Null for the other kinds.</param>
/// <param name="OursTarget">For rename conflicts: ours' rename target; null when ours did not rename.</param>
/// <param name="TheirsTarget">For rename conflicts: theirs' rename target; null when theirs did not rename.</param>
public sealed record MergeConflict(
    string Path,
    MergeConflictKind Kind,
    string? BaseBlob,
    string? OursBlob,
    string? TheirsBlob,
    string? MergedContent,
    string? OursTarget = null,
    string? TheirsTarget = null);
