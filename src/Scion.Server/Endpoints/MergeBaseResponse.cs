namespace Scion.Server.Endpoints;

/// <summary>
/// The merge-base answer: the best base of the two named changesets, or null when their histories
/// are disjoint — a legitimate answer here, not an error (merge is where disjointness becomes a
/// 400, because a merge cannot proceed without a base).
/// </summary>
internal sealed record MergeBaseResponse(string First, string Second, string? MergeBase);
