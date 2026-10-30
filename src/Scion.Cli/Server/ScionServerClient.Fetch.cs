using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Scion.Core;

namespace Scion.Cli.Server;

// The fetch half of the server client — a partial class so the commit-path client
// (ScionServerClient.cs) keeps its own file while the materializer's requests land here. All
// shapes are CLI-local wire records, like the commit half's, never server assembly types.

/// <summary>The trees/missing request for the materializer: a changeset to walk plus its filters.</summary>
internal sealed record TreeEntriesRequest(string ChangesetId, IReadOnlyList<string>? WantedPaths, IReadOnlyList<string>? SparseProfile);

/// <summary>The trees/missing wire response. <see cref="Entries"/> is null only from a server that predates the entry list.</summary>
internal sealed record TreesFetch(string RootTree, IReadOnlyList<string> PresentBlobs, IReadOnlyList<string> MissingBlobs, IReadOnlyList<FetchedPath>? Entries);

/// <summary>The validated trees/missing answer: the entries the materializer writes from are guaranteed present.</summary>
internal sealed record TreeEntries(string RootTree, IReadOnlyList<FetchedPath> Entries, IReadOnlyList<string> MissingBlobs);

/// <summary>One in-scope path of the walked tree: where to write it, which blob, and as what kind.</summary>
internal sealed record FetchedPath(string Path, string BlobHash, FileContentKind Kind);

/// <summary>The blobs/missing request body: the content addresses the client wants.</summary>
internal sealed record MissingBlobsPayload(IReadOnlyList<string> Blobs);

internal sealed partial class ScionServerClient
{
    /// <summary>
    /// Asks the server to walk the tree at <paramref name="changesetId"/> within the wanted paths
    /// and answer the in-scope path-to-blob mapping plus which referenced blobs its store lacks.
    /// </summary>
    public async Task<TreeEntries> TreeEntriesAsync(
        string changesetId,
        IReadOnlyList<string>? wantedPaths = null,
        IReadOnlyList<string>? sparseProfile = null,
        CancellationToken cancellationToken = default)
    {
        using var body = JsonBody(new TreeEntriesRequest(changesetId, wantedPaths, sparseProfile));
        using var response = await client.PostAsync("rpc/trees/missing", body, cancellationToken);
        await EnsureSuccessAsync(response, "tree lookup", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TreesFetch>(JsonOptions, cancellationToken);
        return result?.Entries is null
            ? throw new ScionServerException(
                response.StatusCode,
                "The server's trees/missing response carried no tree entries; it predates the fetch protocol the materializer needs.")
            : new TreeEntries(result.RootTree, result.Entries, result.MissingBlobs);
    }

    /// <summary>The server's newest changeset id — where a fresh working copy checks out from — or null when the log is empty.</summary>
    public async Task<string?> NewestChangesetIdAsync(CancellationToken cancellationToken = default)
    {
        var page = await LogAsync(null, null, 1, cancellationToken);
        return page.Changesets is { Length: > 0 } entries ? entries[0].Id : null;
    }

    /// <summary>
    /// Fetches the named blobs as a streamed, hash-keyed response; each entry is verified against
    /// its own address as it drains, so a corrupt or truncated transfer fails the read.
    /// </summary>
    public async IAsyncEnumerable<BlobFetchEntry> FetchBlobsAsync(
        IReadOnlyList<string> hashes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (hashes.Count == 0)
        {
            yield break;
        }

        // ResponseHeadersRead: the point of this endpoint is streaming — the body is consumed
        // entry by entry below, never buffered whole.
        using var request = new HttpRequestMessage(HttpMethod.Post, "rpc/blobs/missing")
        {
            Content = JsonBody(new MissingBlobsPayload(hashes)),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "blob fetch", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var entry in BlobFetchStream.ReadEntriesAsync(stream, cancellationToken))
        {
            yield return entry;
        }
    }
}
