using System.Text;
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
/// The query half of the source-control API (spec D3): the delta between any two changesets'
/// tree snapshots, and a path's per-line blame at one changeset. Both are pure server-side
/// queries over derived data — the client names objects, the server computes from the
/// tree-snapshot and path-history indices plus the line diff; no working copy and no local
/// graph walk (client–server asymmetry). Every resolution goes through the stores' validated
/// readers and the engine's point queries, never raw SQL at the endpoint.
/// </summary>
internal sealed class QueryDiffBlameEndpoints(
    ChangesetLog changesetLog,
    DerivedDataEngine derivedData,
    BlobStore blobs) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rpc/diff", (string? from, string? to) =>
        {
            if (string.IsNullOrWhiteSpace(from))
            {
                throw new ScionValidationException("Query 'from' is required: the changeset the diff starts from.");
            }

            if (string.IsNullOrWhiteSpace(to))
            {
                throw new ScionValidationException("Query 'to' is required: the changeset the diff ends at.");
            }

            // Any two changesets diff, related or not: the trees are content-addressed states,
            // and the delta between them is well-defined regardless of ancestry. Resolve against
            // the log first, so a client naming an absent changeset gets the 404 the error
            // contract owes it (spec C7) rather than an engine internals message.
            var before = TreeAt(ResolveExisting(from).Id);
            var after = TreeAt(ResolveExisting(to).Id);
            return Results.Json(new DiffResponse(from, to, [.. TreeDiff.Diff(before, after)]));
        })
        .WithTags("diff-blame")
        .WithSummary("Delta between any two changesets' trees")
        .WithDescription(
            "Every changed path between two tree snapshots in canonical change order — the same shape a changeset's own delta " +
            "carries. Any two changesets diff, related or not: the trees are content-addressed states. Renames appear as the " +
            "old path's deletion plus a renamed entry on the new path. Answered server-side from derived data; no local walk.")
        .Produces<DiffResponse>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        endpoints.MapGet("/rpc/blame", (string? changeset, string? path) =>
        {
            if (string.IsNullOrWhiteSpace(changeset))
            {
                throw new ScionValidationException("Query 'changeset' is required: the changeset to blame at.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ScionValidationException("Query 'path' is required: the path whose lines to blame.");
            }

            var head = ResolveExisting(changeset);

            // The tree is the existence authority: a path absent from it — never touched, or
            // deleted before this changeset — has no lines to blame. The path-history chain is
            // non-empty exactly when the tree contains the path: both fold first-parent lineage.
            if (!TreeAt(changeset).TryGet(path, out var headEntry))
            {
                throw new ScionNotFoundException("path", path);
            }

            // The path-history index answers which changesets touched the path, oldest first —
            // the version chain blame walks. The chain's names resolve backward through the
            // deltas: a rename moved the file, so versions before it answer under the pre-rename
            // name (Blame.PreviousName), not under the queried path. A version where the file is
            // absent (deleted, pending re-add) models as empty content, so the line diff
            // re-attributes everything a re-add brings back.
            var history = PathHistoryValue
                .Deserialize(derivedData.Get(PathHistoryDerivedData.TypeName, changeset).Payload.Span)
                .HistoryOf(path);

            // The queried changeset may postdate the last touch: its own tree is the final word
            // on the file's content, so it closes the version list when it did not touch the path.
            List<string> touched = [.. history];
            if (touched.Count == 0 || touched[^1] != head.Id)
            {
                touched.Add(head.Id);
            }

            var names = new string[touched.Count];
            names[^1] = path;
            for (var i = touched.Count - 1; i > 0; i--)
            {
                names[i - 1] = Blame.PreviousName(ResolveExisting(touched[i]).Changes, names[i]);
            }

            List<Blame.BlameVersion> versions = [];
            for (var i = 0; i < touched.Count; i++)
            {
                var versionEntry = TreeAt(touched[i]).TryGet(names[i], out var found)
                    ? found!
                    : null;
                versions.Add(new Blame.BlameVersion(touched[i], versionEntry is null ? string.Empty : ContentOf(versionEntry)));
            }

            var lines = Blame.Annotate([.. versions]);
            return Results.Json(new BlameResponse(
                changeset,
                path,
                [.. lines.Select(line => new BlameLineResponse(line.Line, line.ChangesetId, line.Content))]));
        })
        .WithTags("diff-blame")
        .WithSummary("Per-line blame at one changeset")
        .WithDescription(
            "Every line of the file's content at the named changeset, each with the changeset that introduced it — the " +
            "path-history chain walked through the line diff, server-side. Line content keeps its terminating newline. " +
            "Rename chains resolve backward through pre-rename names automatically.")
        .Produces<BlameResponse>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    /// <summary>Fetches a changeset the client named by id; an absent one is a 404 (spec C7).</summary>
    private Changeset ResolveExisting(string id)
    {
        return changesetLog.TryGetById(id, out var changeset)
            ? changeset!
            : throw new ScionNotFoundException("changeset", id);
    }

    /// <summary>The materialized tree at a changeset, from the tree-snapshots derived index.</summary>
    private TreeSnapshot TreeAt(string changesetId)
    {
        return TreeSnapshotDerivedData.Decode(derivedData.Get(TreeSnapshotDerivedData.TypeName, changesetId));
    }

    private string ContentOf(TreeEntry entry)
    {
        return Encoding.UTF8.GetString(blobs.Read(entry.BlobHash).Content.Span);
    }
}
