using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scion.Core;
using Scion.Core.Derived;
using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// The server-side history operations (spec D3): merge-base answered over the two tips' closed
/// ancestry, and merge / rebase performed entirely server-side, producing changesets — the
/// client–server asymmetry the spec's table commits to ("merge / rebase: server-side; result is
/// new changesets, nothing to push"). The client names changeset ids; the server materializes
/// trees from the derived store, merges against the blob store's content, and appends the result
/// to the write-ahead log.
/// </summary>
/// <remarks>
/// <para>
/// Conflict policy follows each operation's core semantics, and every conflict response carries
/// the spec-C7 vocabulary: path, kind, the blobs on each side, and — for content conflicts — the
/// conflict-marked merged text, so an agent resolves without recomputing anything.
/// </para>
/// <para>
/// A conflicted <c>merge</c> appends nothing and answers 409: the merged tree keeps ours' state
/// on conflicted paths and is not committable until they resolve, and the resolution is the
/// caller's own changeset created through <c>POST /rpc/changesets</c>. A conflicted
/// <c>rebase</c> appends the whole replayed chain and answers 201 with the conflicts attached:
/// replay never aborts — every source changeset yields exactly one rebased changeset, conflicted
/// paths keeping the new base's state — and appending the chain is what moves the lineage; the
/// caller resolves forward.
/// </para>
/// <para>
/// Both write paths keep the create-changeset invariant: synthesized content is stored before the
/// log entry that references it, and an optional ref move is a compare-and-swap applied after the
/// append, deliberately decoupled — a lost move answers 409 with the ref's current value while
/// the changesets stay stored.
/// </para>
/// </remarks>
internal sealed class QueryMergeEndpoints(
    ChangesetLog changesetLog,
    RefStore refStore,
    BlobStore blobStore,
    DerivedDataEngine derivedData) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rpc/merge-base", (string? first, string? second) =>
        {
            var firstId = RequireId(first, "Query 'first' is required: the first changeset's id.");
            var secondId = RequireId(second, "Query 'second' is required: the second changeset's id.");
            ResolveExisting(firstId);
            ResolveExisting(secondId);

            // Disjoint histories are a legitimate answer here — null — not an error: the client
            // asked what the base is, and there is none. Merge, which cannot proceed without a
            // base, is where disjointness becomes a 400.
            var graph = BuildClosedAncestry(firstId, secondId);
            return Results.Json(new MergeBaseResponse(firstId, secondId, MergeBasePolicy.Select(graph, firstId, secondId)));
        });

        endpoints.MapPost("/rpc/merge", async (MergeRequest request, CancellationToken cancellationToken) =>
        {
            var oursId = RequireId(request.Ours, "Body 'ours' is required: the changeset to merge into.");
            var theirsId = RequireId(request.Theirs, "Body 'theirs' is required: the changeset to merge.");
            var (message, author) = ValidateCommitMetadata(request.Message, request.Author);
            var ours = ResolveExisting(oursId);
            var theirs = ResolveExisting(theirsId);

            string mergeBase;
            try
            {
                mergeBase = MergeBasePolicy.SelectRequired(BuildClosedAncestry(ours.Id, theirs.Id), ours.Id, theirs.Id);
            }
            catch (InvalidOperationException failure)
            {
                // Disjoint histories: there is no base to diff from, so the merge cannot run.
                throw new ScionValidationException(failure.Message);
            }

            var oursTree = TreeAt(ours.Id);
            var theirsTree = TreeAt(theirs.Id);
            var baseTree = TreeAt(mergeBase);

            ThreeWayMergeResult merge;
            try
            {
                merge = ThreeWayMerge.Merge(baseTree, oursTree, theirsTree, ContentText);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or BlobStoreException)
            {
                throw new ScionValidationException(failure.Message);
            }

            if (!merge.IsClean)
            {
                MergeConflictResponse[] conflicts = [.. merge.Conflicts.Select(MergeConflictResponse.From)];
                return Results.Json(
                    new MergeResponse(mergeBase, Id: null, Ref: null, Conflicts: conflicts),
                    statusCode: StatusCodes.Status409Conflict);
            }

            await StoreSynthesizedBlobs(merge.MergedContents, cancellationToken);
            var mergeChangeset = Changeset.Create(
                [ours.Id, theirs.Id],
                TreeDiff.Diff(oursTree, merge.Tree),
                message,
                author,
                request.TimestampUtc);
            await StoreFailures.RunAsync(() =>
            {
                changesetLog.Append(mergeChangeset);
                return Task.CompletedTask;
            });

            var refState = ApplyRefMove(request.RefMove, mergeChangeset.Id);
            return Results.Json(
                new MergeResponse(mergeBase, mergeChangeset.Id, refState, []),
                statusCode: StatusCodes.Status201Created);
        });

        endpoints.MapPost("/rpc/rebase", async (RebaseRequest request, CancellationToken cancellationToken) =>
        {
            var startId = RequireId(request.Start, "Body 'start' is required: the oldest changeset of the chain to replay.");
            var tipId = RequireId(request.Tip, "Body 'tip' is required: the newest changeset of the chain to replay.");
            var newBaseId = RequireId(request.NewBase, "Body 'newBase' is required: the changeset the chain rebases onto.");
            var start = ResolveExisting(startId);
            var tip = ResolveExisting(tipId);
            ResolveExisting(newBaseId);

            var chain = FirstParentChain(start, tip);
            var oldBaseId = chain[0].Parents.FirstOrDefault();
            var oldBaseTree = oldBaseId is null ? TreeSnapshot.Empty : TreeAt(oldBaseId);
            var newBaseTree = TreeAt(newBaseId);

            RebaseResult result;
            try
            {
                result = Rebase.Replay(oldBaseTree, chain, newBaseId, newBaseTree, ContentText);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or BlobStoreException)
            {
                throw new ScionValidationException(failure.Message);
            }

            await StoreSynthesizedBlobs(result.MergedBlobs, cancellationToken);
            await StoreFailures.RunAsync(() =>
            {
                changesetLog.AppendRange(result.Rebased);
                return Task.CompletedTask;
            });

            var refState = ApplyRefMove(request.RefMove, result.Rebased[^1].Id);
            RebasedChangesetResponse[] rebased =
                [.. result.Rebased.Select((changeset, index) => new RebasedChangesetResponse(chain[index].Id, changeset.Id))];
            RebaseConflictResponse[] conflicts = [.. result.Conflicts.Select(RebaseConflictResponse.From)];
            return Results.Json(
                new RebaseResponse(newBaseId, rebased, conflicts, refState),
                statusCode: StatusCodes.Status201Created);
        });
    }

    /// <summary>
    /// The skip graph over the closed ancestry of the named changesets — every changeset reachable
    /// over parent edges, all of whose parents are therefore inside the snapshot, the closed shape
    /// <see cref="SkipGraph.Build"/> requires. A parent that never reached the log (the log permits
    /// dangling parents) leaves the ancestry incomplete: that is a 400, not a silent partial graph.
    /// </summary>
    private SkipGraph BuildClosedAncestry(params string[] tips)
    {
        var collected = new Dictionary<string, Changeset>(StringComparer.Ordinal);
        var queue = new Queue<string>(tips);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (collected.ContainsKey(id))
            {
                continue;
            }

            if (!changesetLog.TryGetById(id, out var changeset) || changeset is null)
            {
                throw new ScionValidationException(
                    $"Changeset '{id}' is named in an ancestry but is not in the log; the DAG snapshot is incomplete and the operation cannot proceed.");
            }

            collected.Add(id, changeset);
            foreach (var parent in changeset.Parents)
            {
                queue.Enqueue(parent);
            }
        }

        return SkipGraph.Build(collected.Values.ToArray());
    }

    /// <summary>
    /// The first-parent path from <paramref name="start"/> (inclusive) to <paramref name="tip"/>
    /// (inclusive), oldest first — the order <see cref="Rebase.Replay"/> replays: each element's
    /// first parent is the element before it, and the chain's first element's first parent (the
    /// old base) sits outside the chain. A tip that does not extend start along first parents, or
    /// a dangling parent between the two, is a 400: the chain to replay is not well-defined.
    /// </summary>
    private Changeset[] FirstParentChain(Changeset start, Changeset tip)
    {
        List<Changeset> walked = [tip];
        var current = tip;
        while (!current.Id.Equals(start.Id, StringComparison.Ordinal))
        {
            var parentId = current.Parents.FirstOrDefault();
            if (parentId is null || !changesetLog.TryGetById(parentId, out var parent) || parent is null)
            {
                throw new ScionValidationException(
                    $"Changeset '{start.Id}' is not reachable from '{tip.Id}' along first parents; rebase replays the first-parent chain from start to tip, so tip must extend start.");
            }

            current = parent;
            walked.Add(current);
        }

        walked.Reverse();
        return [.. walked];
    }

    /// <summary>
    /// The materialized tree at a changeset — the tree-snapshots derived value, decoded. The id
    /// resolves through the log first, so an absent changeset (including a dangling parent named
    /// inside a stored one) is the 404 the contract owes (spec C7), not an engine internals error.
    /// </summary>
    private TreeSnapshot TreeAt(string changesetId)
    {
        if (!changesetLog.TryGetById(changesetId, out _))
        {
            throw new ScionNotFoundException("changeset", changesetId);
        }

        return TreeSnapshotDerivedData.Decode(derivedData.Get(TreeSnapshotDerivedData.TypeName, changesetId));
    }

    /// <summary>Blob text for the merge's content lookup — UTF-8, per the core convention.</summary>
    private string ContentText(string hash)
    {
        return Encoding.UTF8.GetString(blobStore.Read(hash).Content.Span);
    }

    /// <summary>
    /// Stores synthesized content — clean line-level merges neither side had before — before the
    /// log entry that references it (the create-changeset invariant: a crash may leave an
    /// unreferenced blob, never a changeset pointing at absent content).
    /// </summary>
    private async Task StoreSynthesizedBlobs(IReadOnlyDictionary<string, string> synthesized, CancellationToken cancellationToken)
    {
        foreach (var (_, text) in synthesized)
        {
            var blob = Blob.Create(Encoding.UTF8.GetBytes(text));
            await StoreFailures.RunAsync(() => blobStore.WriteAsync(blob, cancellationToken));
        }
    }

    private RefState? ApplyRefMove(MergeRequest.RefMoveRequest? move, string newTarget)
    {
        return move is null ? null : CompareAndSwapRef(move.Name, move.ExpectedRevision, newTarget);
    }

    private RefState? ApplyRefMove(RebaseRequest.RefMoveRequest? move, string newTarget)
    {
        return move is null ? null : CompareAndSwapRef(move.Name, move.ExpectedRevision, newTarget);
    }

    /// <summary>
    /// The compare-and-swap ref move that publishes the appended head, deliberately decoupled from
    /// the append: losing the race answers 409 with the ref's current value while the changesets
    /// stay stored (spec C7).
    /// </summary>
    private RefState? CompareAndSwapRef(string name, long expectedRevision, string newTarget)
    {
        if (refStore.TryCompareAndSwap(name, expectedRevision, newTarget, out var current))
        {
            return current;
        }

        // A null current state means the move expected a ref that does not exist — there is no
        // value to carry, so report the creation revision (zero) and an empty target: the
        // vocabulary that tells a client the ref must be created.
        throw current is null
            ? new RefConflictException(name, currentTarget: string.Empty, currentRevision: RefStore.CreationRevision)
            : new RefConflictException(name, current.Target, current.Revision);
    }

    /// <summary>Fetches a changeset the client named by id; an absent one is a 404 (spec C7).</summary>
    private Changeset ResolveExisting(string id)
    {
        return changesetLog.TryGetById(id, out var changeset) && changeset is not null
            ? changeset
            : throw new ScionNotFoundException("changeset", id);
    }

    /// <summary>The commit metadata a server-produced changeset must carry (create-changeset's contract).</summary>
    private static (string Message, Author Author) ValidateCommitMetadata(string? message, Author? author)
    {
        if (message is null)
        {
            throw new ScionValidationException("Merge 'message' is required (an empty string is allowed).");
        }

        if (author is null || author.Name is null || author.Email is null)
        {
            throw new ScionValidationException("Merge 'author' is required with both 'name' and 'email'.");
        }

        return (message, author);
    }

    private static string RequireId(string? value, string complaint)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ScionValidationException(complaint)
            : value;
    }
}
