using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// The blob-fetch endpoint (spec D3, fetch protocol) — the read half of blob transfer. The client
/// names the content addresses its cache lacks; the server answers with a hash-keyed stream of
/// those blobs as application/octet-stream. Existence is settled up front: a request naming an
/// address the store lacks is a 404 problem response (spec C7), never a hole in the stream — the
/// server is authoritative, so a client should never have heard of a blob this server does not have.
/// </summary>
/// <remarks>
/// <para>
/// Wire format, one entry per requested hash in request order (duplicates served once):
/// <c>entry := hash-key chunk-sequence</c>, where <c>hash-key</c> is 64 ASCII bytes of lowercase
/// hex and each chunk is <c>length[8 bytes, unsigned little-endian] bytes[length]</c>; a
/// zero-length chunk closes the entry. The chunks spell the blob's raw content.
/// </para>
/// <para>
/// Chunks rather than one whole-blob length prefix, because blobs rest zlib-compressed and zlib
/// does not disclose a decompressed length before decoding — the stream cannot know the size it is
/// about to send without buffering the blob whole, which is exactly what streaming forbids. Each
/// entry is verified as it drains: the server hashes the content it serves and aborts the response
/// if the digest does not match the addressed hash, so a corrupted store yields a truncated stream,
/// never wrong bytes presented as right. Clients re-verify regardless — content addressing means
/// the reader always knows the hash it asked for.
/// </para>
/// </remarks>
internal sealed class FetchBlobsEndpoints(BlobStore blobStore) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Literal segment: ASP.NET Core routing prefers it over /rpc/blobs/{hash}, so the two
        // blob routes coexist regardless of registration order.
        endpoints.MapPost(
            "/rpc/blobs/missing",
            async (MissingBlobsRequest? request, HttpContext context, CancellationToken cancellationToken) =>
            {
                var requested = DistinctValidHashes(request);

                foreach (var hash in requested)
                {
                    if (!blobStore.Exists(hash))
                    {
                        throw new ScionNotFoundException("blob", hash);
                    }
                }

                var response = context.Response;
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "application/octet-stream";

                foreach (var hash in requested)
                {
                    await using var source = blobStore.OpenReadStream(hash);
                    try
                    {
                        await BlobFetchFraming.WriteVerifiedEntryAsync(response.Body, hash, source, cancellationToken);
                    }
                    catch (InvalidDataException failure)
                    {
                        // Undecodable stored bytes surface on the first read, wrapped to the same
                        // vocabulary the buffered read uses. The response may already have started,
                        // in which case this aborts the stream mid-flight — clients verify hashes
                        // and see the truncation, never silently-wrong content.
                        throw new BlobHashMismatchException(hash, actualHash: null, failure);
                    }
                }
            });
    }

    /// <summary>The requested hashes, malformed ones rejected, deduplicated in first-seen order.</summary>
    private static IReadOnlyList<string> DistinctValidHashes(MissingBlobsRequest? request)
    {
        List<string> requested = [];
        HashSet<string> seen = [];
        foreach (var raw in request?.Blobs ?? [])
        {
            if (raw is null || raw.Length != 64 || !raw.All(Uri.IsHexDigit))
            {
                throw new ScionValidationException($"Blob hash '{raw}' is not a 64-character hex SHA-256.");
            }

            var hash = raw.ToLowerInvariant();
            if (seen.Add(hash))
            {
                requested.Add(hash);
            }
        }

        return requested;
    }
}

/// <summary>Record for <see cref="MissingBlobsRequest.Blobs"/>'s payload: the addresses a client's cache lacks.</summary>
/// <param name="Blobs">Content addresses to fetch; an absent or empty list fetches nothing.</param>
internal sealed record MissingBlobsRequest(IReadOnlyList<string>? Blobs);

/// <summary>Writes the fetch stream's framed entries; the format is specified on <see cref="FetchBlobsEndpoints"/>.</summary>
internal static class BlobFetchFraming
{
    /// <summary>Largest copy chunk, sized to stay off the large-object heap — the bound that keeps
    /// a huge blob from ever being whole in memory.</summary>
    private const int ChunkBytes = 80 * 1024;

    /// <summary>
    /// Streams one blob into the response as a framed entry: the hash key, the content in
    /// length-prefixed chunks, the zero-length close. Served content is hashed as it flows; a
    /// digest that does not match the addressed hash throws rather than completing the entry.
    /// </summary>
    public static async Task WriteVerifiedEntryAsync(
        Stream response,
        string addressedHash,
        Stream content,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(Encoding.ASCII.GetBytes(addressedHash), cancellationToken);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var prefix = new byte[sizeof(ulong)];
        var chunk = new byte[ChunkBytes];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            hasher.AppendData(chunk.AsSpan(0, read));
            BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)read);
            await response.WriteAsync(prefix, cancellationToken);
            await response.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(prefix, 0);
        await response.WriteAsync(prefix, cancellationToken);

        var computed = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!computed.Equals(addressedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new BlobHashMismatchException(addressedHash, computed);
        }
    }
}
