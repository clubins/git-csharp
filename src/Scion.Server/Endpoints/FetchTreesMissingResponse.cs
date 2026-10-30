namespace Scion.Server.Endpoints;

/// <summary>
/// The trees/missing response: the walked tree's content hash, the blob hashes the walk found in
/// scope and present in the blob store, and the missing set — referenced by in-scope paths but
/// absent from the store — the client must upload before shipping a changeset that references
/// them, or cannot fetch. Both lists are distinct, lowercase, and ordinal-sorted. A rooted walk
/// has no "missing trees": the tree is materialized from the log before the walk, so an unknown
/// root is the 404 case, not a response field.
/// </summary>
public sealed record FetchTreesMissingResponse(string RootTree, IReadOnlyList<string> PresentBlobs, IReadOnlyList<string> MissingBlobs);
