using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Scion.Core;
using Scion.Core.Derived;
using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// The trees/missing fetch endpoint (spec D3) — the server's half of the wanted-object fetch
/// protocol. The client never negotiates what it has: it names the tree it wants (by changeset
/// id or by the tree's own content hash) and the paths it wants it under, and the server walks
/// its own snapshot — scoped by the wanted paths and the sparse profile — reporting which of the
/// referenced blob hashes it holds and which it lacks.
/// </summary>
/// <remarks>
/// A rooted walk cannot have "missing" subtrees: the tree is materialized from the changeset log
/// before the walk, so an unknown root is the 404 case. Blob content, though, can be absent on
/// disk — the crash window between log append and blob write (spec C6) — and reporting those in
/// the missing set beats failing a walk the client asked for. The walk itself is pure
/// (<see cref="TreeWalk.Collect"/>); this module resolves the root and answers presence.
/// </remarks>
internal sealed class FetchTreesEndpoints(
    ChangesetLog changesetLog,
    DerivedDataStore derivedDataStore,
    DerivedDataEngine derivedDataEngine,
    BlobStore blobStore) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rpc/trees/missing", async (FetchTreesMissingRequest request) =>
        {
            ValidatePatterns(request);
            var (snapshot, rootTree) = await ResolveRootAsync(request);

            var walked = TreeWalk.Collect(snapshot, request.WantedPaths, request.SparseProfile);
            var missing = await StoreFailures.RunAsync(() => Task.FromResult(blobStore.Missing(walked.BlobHashes)));
            var missingSet = missing.ToHashSet(StringComparer.Ordinal);
            var present = walked.BlobHashes.Where(hash => !missingSet.Contains(hash)).ToList();

            return Results.Json(new FetchTreesMissingResponse(rootTree, present, missing, walked.Entries));
        })
        .WithTags("fetch")
        .WithSummary("Report which blobs a tree walk still needs")
        .WithDescription(
            "The server's half of the wanted-object fetch protocol: name the tree by changeset id or by the tree snapshot's own " +
            "hash (exactly one), plus the glob patterns that scope the walk. The server walks its own snapshot and reports which " +
            "referenced blob hashes it holds (presentBlobs) and which it lacks (missingBlobs). A client that wants files asks this " +
            "first, then fetches only the missing blobs — never a full checkout.")
        .Produces<FetchTreesMissingResponse>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Resolves the request's exactly-one tree identifier into the snapshot to walk and its
    /// content hash. Request-shape rejections are 400s; a well-formed name for something the
    /// server does not hold is a 404 — the fetch protocol's "ask for what you want" contract
    /// answering plainly that the object does not exist.
    /// </summary>
    private async Task<(TreeSnapshot Snapshot, string RootTree)> ResolveRootAsync(FetchTreesMissingRequest request)
    {
        if ((request.ChangesetId is null) == (request.RootTree is null))
        {
            throw new ScionValidationException(
                "Name exactly one tree to walk: 'changesetId' (the tree at that changeset) or 'rootTree' (the tree snapshot's own content hash).");
        }

        if (request.ChangesetId is { } changesetId)
        {
            RequireSha256Hex(changesetId, "changesetId");
            var changesetKey = changesetId.ToLowerInvariant();
            if (!changesetLog.TryGetById(changesetKey, out _))
            {
                throw new ScionNotFoundException("changeset", changesetId);
            }

            var value = await StoreFailures.RunAsync(() =>
                Task.FromResult(derivedDataEngine.Get(TreeSnapshotDerivedData.TypeName, changesetKey)));
            return (TreeSnapshotDerivedData.Decode(value), value.Hash);
        }

        var requestedTree = request.RootTree!;
        RequireSha256Hex(requestedTree, "rootTree");
        var treeValue = await StoreFailures.RunAsync(() =>
            Task.FromResult(
                derivedDataStore.TryGetByValueHash(TreeSnapshotDerivedData.TypeName, requestedTree, out var found)
                    ? found
                    : null));
        if (treeValue is null)
        {
            throw new ScionNotFoundException("tree", requestedTree);
        }

        return (TreeSnapshotDerivedData.Decode(treeValue), treeValue.Hash);
    }

    private static void ValidatePatterns(FetchTreesMissingRequest request)
    {
        ValidatePatternList(request.WantedPaths, "wantedPaths");
        ValidatePatternList(request.SparseProfile, "sparseProfile");
    }

    private static void ValidatePatternList(List<string>? patterns, string fieldName)
    {
        if (patterns is null)
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ScionValidationException($"Every entry in '{fieldName}' must be a non-empty glob pattern.");
            }
        }
    }

    /// <summary>Rejects anything that is not a 64-character hex SHA-256 — the only shape a content address has.</summary>
    private static void RequireSha256Hex(string value, string fieldName)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ScionValidationException($"'{fieldName}' must be a 64-character hex SHA-256, got '{value}'.");
        }
    }
}
