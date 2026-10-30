using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scion.Core;
using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// The blob-upload endpoint (spec D3): stores one blob under its content address. The route names
/// the expected SHA-256 and the body is the raw content; the server verifies the body hashes to
/// the named address before storing, so content can never be stored under an address it does not
/// match. Re-uploads of already-stored content are no-ops — the store deduplicates by address —
/// which makes uploads safe to retry and lets parallel agents converge on one stored file.
/// Status contract: 201 when this request newly stored the blob, 200 when it already existed.
/// </summary>
internal sealed class BlobEndpoints(BlobStore blobStore) : IScionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rpc/blobs/{hash}", async (string hash, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!IsSha256Hex(hash))
            {
                throw new ScionValidationException($"Blob hash '{hash}' is not a 64-character hex SHA-256.");
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            var content = buffer.ToArray();

            var computed = Sha256.HashHex(content);
            if (!computed.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ScionValidationException($"Content does not match its address: the body hashes to '{computed}', not '{hash}'.");
            }

            var isNew = !blobStore.Exists(computed);
            var stored = await StoreFailures.RunAsync(() => blobStore.WriteAsync(new Blob(computed, content), cancellationToken));
            return Results.Json(
                new BlobUploadResponse(stored),
                statusCode: isNew ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });
    }

    /// <summary>True for 64 hex characters — the only acceptable shape for a content address.</summary>
    private static bool IsSha256Hex(string? value)
    {
        return value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
