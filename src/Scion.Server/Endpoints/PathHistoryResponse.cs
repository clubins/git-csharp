namespace Scion.Server.Endpoints;

/// <summary>
/// A path's history at one changeset: the changesets that touched it, oldest first — the
/// path-history derived index's own chain order, unmodified. A path never touched as of the
/// named changeset answers an empty list.
/// </summary>
internal sealed record PathHistoryResponse(string Path, string[] Changesets);
