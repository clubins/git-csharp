namespace Scion.Core;

/// <summary>
/// The outcome of a line-level three-way merge (<see cref="ThreeWayMerge.MergeText"/>):
/// <see cref="Text"/> is the merged text — clean when <see cref="Clean"/>, otherwise carrying
/// conflict markers around the regions the sides could not reconcile.
/// </summary>
public sealed record LineMergeResult(bool Clean, string Text);
