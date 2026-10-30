using System.Collections.Immutable;
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
/// The create-changeset endpoint (spec D3) — the write half of a commit. The client sends the
/// changeset record (parents, changes relative to the first parent, message, author, timestamp)
/// plus the changed blobs it references, and optionally a ref move to publish the commit. The
/// server re-derives the changeset id from the canonical payload and rejects mismatches (400),
/// stores every blob before appending — content first, then the log entry that references it,
/// so a crash can leave an unreferenced blob but never a changeset pointing at absent content —
/// validates that the delta applies to the first parent's snapshot, and appends to the
/// write-ahead log idempotently.
/// </summary>
/// <remarks>
/// A ref move is a compare-and-swap applied after the append, deliberately decoupled from it:
/// losing the race answers 409 with the ref's current value (spec C7) while the changeset stays
/// stored, unreachable until a rebased retry or another commit references it. No other endpoint
/// and no lock sits between append and CAS, so parallel agents never contend on commit.
/// </remarks>
internal sealed class ChangesetEndpoints(
    ChangesetLog changesetLog,
    RefStore refStore,
    BlobStore blobStore,
    DerivedDataEngine derivedData) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rpc/changesets", async (CreateChangesetRequest request, CancellationToken cancellationToken) =>
        {
            var changeset = ValidateAndRebuild(request);

            foreach (var payload in request.Blobs ?? [])
            {
                if (payload.Content is null)
                {
                    throw new ScionValidationException($"Blob '{payload.Hash}' carries no content.");
                }

                await StoreFailures.RunAsync(() => blobStore.WriteAsync(new Blob(payload.Hash, payload.Content), cancellationToken));
            }

            var referenced = (request.Changes ?? [])
                .SelectMany(change => new[] { change.OldBlob, change.NewBlob })
                .Where(hash => hash is not null)
                .Select(hash => hash!);
            var missing = await StoreFailures.RunAsync(() => Task.FromResult(blobStore.Missing(referenced)));
            if (missing.Count > 0)
            {
                throw new ScionValidationException(
                    $"Changeset references blob content that was never stored: {string.Join(", ", missing)}. Upload the blobs with (or before) the changeset that references them.");
            }

            ValidateDeltaApplies(changeset);

            await StoreFailures.RunAsync(() =>
            {
                changesetLog.Append(changeset);
                return Task.CompletedTask;
            });

            RefState? refState = null;
            if (request.RefMove is { } move)
            {
                if (!refStore.TryCompareAndSwap(move.Name, move.ExpectedRevision, changeset.Id, out var current))
                {
                    // A null current state means the move expected a ref that does not exist —
                    // there is no value to carry, so report the creation revision (zero) and an
                    // empty target: the vocabulary that tells a client the ref must be created.
                    throw current is null
                        ? new RefConflictException(move.Name, currentTarget: string.Empty, currentRevision: RefStore.CreationRevision)
                        : new RefConflictException(move.Name, current.Target, current.Revision);
                }

                refState = current;
            }

            return Results.Json(new CreateChangesetResponse(changeset.Id, refState), statusCode: StatusCodes.Status201Created);
        })
        .WithTags("changesets")
        .WithSummary("Append a changeset to the write-ahead log")
        .WithDescription(
            "The write half of a commit: the client sends the changeset record plus the changed blobs it references, and the " +
            "server re-derives the id from the canonical payload (a mismatch is rejected with 400), stores every blob before " +
            "appending, and appends idempotently. The optional ref move is a compare-and-swap applied after the append: losing " +
            "the race answers 409 with the ref's current value while the changeset stays stored — commit never contends on a lock.")
        .Produces<CreateChangesetResponse>(StatusCodes.Status201Created)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// A delta that cannot apply to the first parent's snapshot — a Modified or Deleted path the
    /// parent's tree does not contain, an Added path it already has, an OldBlob mismatch — would
    /// leave the tree at this changeset uncomputable: every later read (diff, merge, checkout)
    /// would fail. Rejected here with the stable validation problem instead. A dangling first
    /// parent is the tolerated out-of-order append state — there is no parent tree to apply
    /// against, so nothing to reject either; the sweep leaves that changeset until its parent
    /// lands.
    /// </summary>
    private void ValidateDeltaApplies(Changeset changeset)
    {
        if (changeset.Parents.IsEmpty || !changesetLog.TryGetById(changeset.Parents[0], out _))
        {
            return;
        }

        var parentTree = TreeSnapshotDerivedData.Decode(
            derivedData.Get(TreeSnapshotDerivedData.TypeName, changeset.Parents[0]));
        try
        {
            parentTree.Apply(changeset.Changes);
        }
        catch (ArgumentException failure)
        {
            throw new ScionValidationException(failure.Message);
        }
    }

    /// <summary>
    /// Rebuilds the changeset from the request's payload fields — <see cref="Changeset.Create"/>
    /// canonicalizes and hashes them — then compares the derived id against the client's. Every
    /// rejection here is a 400; anything that survives is exactly the changeset the client claims
    /// to have hashed.
    /// </summary>
    private static Changeset ValidateAndRebuild(CreateChangesetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new ScionValidationException("Changeset 'id' is required: the SHA-256 of the canonical payload encoding.");
        }

        if (request.Message is null)
        {
            throw new ScionValidationException("Changeset 'message' is required (an empty string is allowed).");
        }

        if (request.Author is null || request.Author.Name is null || request.Author.Email is null)
        {
            throw new ScionValidationException("Changeset 'author' is required with both 'name' and 'email'.");
        }

        if ((request.Parents?.Count ?? 0) > Changeset.MaxParents)
        {
            throw new ScionValidationException($"A changeset has at most {Changeset.MaxParents} parents; got {request.Parents!.Count}.");
        }

        if (request.Parents is not null)
        {
            foreach (var parent in request.Parents)
            {
                if (!IsSha256Hex(parent))
                {
                    throw new ScionValidationException($"Parent '{parent}' is not a 64-character hex SHA-256.");
                }
            }
        }

        if (request.Changes is not null)
        {
            foreach (var change in request.Changes)
            {
                if (change is null || change.Path is null || change.Path.Length == 0)
                {
                    throw new ScionValidationException("Every change needs a path.");
                }
            }
        }

        var changeset = Changeset.Create(
            request.Parents?.ToImmutableArray() ?? [],
            request.Changes?.ToImmutableArray() ?? [],
            request.Message,
            request.Author,
            request.TimestampUtc);

        if (!changeset.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScionValidationException(
                $"Changeset id '{request.Id}' does not match the SHA-256 of the canonical payload ('{changeset.Id}'); ids are re-derived on the server and mismatches are rejected.");
        }

        return changeset;
    }

    /// <summary>True for 64 hex characters — the changeset-id shape every parent id must have.</summary>
    private static bool IsSha256Hex(string? value)
    {
        return value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
