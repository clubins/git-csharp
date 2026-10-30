using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// The trees/missing response: the walked tree's content hash, the blob hashes the walk found in
/// scope and present in the blob store, the missing set — referenced by in-scope paths but absent
/// from the store — and the walk's entries: every in-scope path with its content address and
/// kind, the mapping a working-copy materializer writes from. Both hash lists are distinct,
/// lowercase, and ordinal-sorted; entries enumerate in ordinal path order. A rooted walk has no
/// "missing trees": the tree is materialized from the log before the walk, so an unknown root is
/// the 404 case, not a response field.
/// </summary>
public sealed record FetchTreesMissingResponse(
    string RootTree,
    IReadOnlyList<string> PresentBlobs,
    IReadOnlyList<string> MissingBlobs,
    IReadOnlyList<TreeWalkEntry>? Entries = null);
