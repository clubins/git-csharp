using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scion.Core;
using Scion.Core.Derived;
using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// The read half of the source-control API (spec D3): a stored changeset fetched by id, history
/// paged along first-parent lineage from a tip, and a path's touching changesets answered from
/// the path-history derived index. All three are pure server-side queries — the client names an
/// object and the server answers; no graph is walked on the client (client–server asymmetry).
/// Every resolution goes through the stores' validated readers and the engine's point queries,
/// never raw SQL at the endpoint.
/// </summary>
/// <remarks>
/// The log walks <em>first-parent</em> lineage only — the trunk history the changeset model is
/// built on (deltas are relative to the first parent, and the import criterion pins lineage
/// equivalence to <c>git log --first-parent</c>). A merge's second-parent side stays reachable
/// from its own changeset, not through the walk; a parent that is not in the log ends the walk,
/// the way shallow history ends.
/// </remarks>
internal sealed class ReadEndpoints(ChangesetLog changesetLog, DerivedDataEngine derivedData) : IScionEndpoints
{
    /// <summary>Page size when the client names no limit.</summary>
    private const int DefaultPageSize = 100;

    /// <summary>Upper bound on any page: one request's work is bounded by the page, never the log.</summary>
    private const int MaxPageSize = 1_000;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rpc/changesets/{id}", (string id) =>
        {
            if (!changesetLog.TryGetById(id, out var changeset))
            {
                throw new ScionNotFoundException("changeset", id);
            }

            return Results.Json(ChangesetResponse.From(changeset!));
        });

        endpoints.MapGet("/rpc/log", (string? tip, string? after, string? limit) =>
        {
            var pageSize = ParseLimit(limit);
            var current = StartOfWalk(tip, after);

            List<ChangesetResponse> entries = [];
            while (entries.Count < pageSize && current is not null && changesetLog.TryGetById(current, out var changeset))
            {
                entries.Add(ChangesetResponse.From(changeset!));
                current = changeset!.Parents.FirstOrDefault();
            }

            // The walk ended because the page filled: one more link means a next page (and the
            // cursor that resumes it), while a dangling parent or the log's root means the walk
            // is simply over.
            string? nextAfter = entries.Count == pageSize && current is not null && changesetLog.Contains(current)
                ? entries[^1].Id
                : null;

            return Results.Json(new LogPageResponse([.. entries], nextAfter));
        });

        endpoints.MapGet("/rpc/path-history", (string? changeset, string? path) =>
        {
            if (string.IsNullOrWhiteSpace(changeset))
            {
                throw new ScionValidationException("Query 'changeset' is required: the changeset to answer the path's history at.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ScionValidationException("Query 'path' is required: the path whose history to answer.");
            }

            // The engine is strict by design — an unknown changeset throws its own complaint.
            // Resolve against the log first, so a client naming an absent changeset gets the 404
            // the error contract owes it (spec C7) rather than an engine internals message.
            if (!changesetLog.TryGetById(changeset, out _))
            {
                throw new ScionNotFoundException("changeset", changeset);
            }

            var value = derivedData.Get(PathHistoryDerivedData.TypeName, changeset);
            var history = PathHistoryValue.Deserialize(value.Payload.Span).HistoryOf(path);
            return Results.Json(new PathHistoryResponse(path, [.. history]));
        });
    }

    /// <summary>
    /// Where the walk starts: the resume cursor's first parent when the client carries one, else
    /// the named tip, else the newest appended changeset — null on an empty log. The cursor wins
    /// over the tip, so a client may keep sending both and page on regardless.
    /// </summary>
    private string? StartOfWalk(string? tip, string? after)
    {
        if (after is not null)
        {
            return FirstParentOf(ResolveExisting(after));
        }

        if (tip is not null)
        {
            return ResolveExisting(tip).Id;
        }

        return changesetLog.TryGetNewest(out var newest) ? newest!.Id : null;
    }

    /// <summary>Fetches a changeset the client named by id; an absent one is a 404 (spec C7).</summary>
    private Changeset ResolveExisting(string id)
    {
        return changesetLog.TryGetById(id, out var changeset)
            ? changeset!
            : throw new ScionNotFoundException("changeset", id);
    }

    private static string? FirstParentOf(Changeset changeset)
    {
        return changeset.Parents.FirstOrDefault();
    }

    private static int ParseLimit(string? limit)
    {
        if (limit is null)
        {
            return DefaultPageSize;
        }

        if (!int.TryParse(limit, out var parsed) || parsed is < 1 or > MaxPageSize)
        {
            throw new ScionValidationException($"Query 'limit' must be an integer between 1 and {MaxPageSize}; got '{limit}'.");
        }

        return parsed;
    }
}
