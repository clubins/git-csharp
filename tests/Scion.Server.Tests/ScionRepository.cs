using System.Text;
using System.Text.Json;
using Scion.Core;

namespace Scion.Server.Tests;

/// <summary>
/// The quickstart's own steps as plain HTTP calls, with nothing touched behind the API's back —
/// the same loop docs/agent-api.md walks an agent through. Every method returns the raw response
/// so tests assert the wire contract itself: statuses, content types, body shapes.
/// </summary>
public sealed class ScionRepository
{
    private readonly HttpClient client;

    public ScionRepository(HttpClient client)
    {
        this.client = client;
    }

    /// <summary>Auth: every request carries the bearer token.</summary>
    public static HttpClient WithToken(ScionServerFactory factory)
    {
        return factory.CreateAuthedClient();
    }

    /// <summary>The quickstart's commit step: upload each changed file's blob, then append the changeset.</summary>
    public async Task<HttpResponseMessage> CommitAsync(
        string message,
        IReadOnlyList<FileChange> changes,
        (string Name, long ExpectedRevision)? refMove = null,
        IReadOnlyList<string>? parents = null,
        long timestampUtc = 1_700_000_000,
        string authorName = "Agent",
        string authorEmail = "agent@scion.test")
    {
        foreach (var change in changes.Where(c => c.Content is not null))
        {
            var response = await client.PostAsync(
                $"/rpc/blobs/{Sha256.HashHex(change.Content!)}",
                new ByteArrayContent(change.Content!));
            if (!response.IsSuccessStatusCode)
            {
                return response;
            }
        }

        var payload = new
        {
            id = ChangesetHash(message, changes, parents, timestampUtc, authorName, authorEmail),
            parents = parents?.ToArray() ?? [],
            changes = changes.Select(c => new
            {
                path = c.Path,
                oldBlob = c.OldContent is null ? null : Sha256.HashHex(c.OldContent),
                newBlob = c.Content is null ? null : Sha256.HashHex(c.Content),
                kind = "regular",
                type = c.Content is null ? "deleted" : c.OldContent is null ? "added" : "modified",
            }).ToArray(),
            message,
            author = new { name = authorName, email = authorEmail },
            timestampUtc,
            blobs = Array.Empty<object>(),
            refMove = refMove is null ? null : new { name = refMove.Value.Name, expectedRevision = refMove.Value.ExpectedRevision },
        };
        return await PostJsonAsync("/rpc/changesets", payload);
    }

    /// <summary>Uploads one blob through the documented endpoint (step 2 of the commit loop).</summary>
    public async Task<HttpResponseMessage> UploadAsync(byte[] content)
    {
        return await client.PostAsync($"/rpc/blobs/{Sha256.HashHex(content)}", new ByteArrayContent(content));
    }

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        return await client.GetAsync(path);
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(path, body);
    }

    /// <summary>Sends an object payload in the documented wire format (camelCase, string enums).</summary>
    public async Task<HttpResponseMessage> PostJsonAsync(string path, object payload)
    {
        return await PostJsonAsync(path, JsonSerializer.Serialize(payload, TestJson.Options));
    }

    /// <summary>
    /// Re-derives the changeset id exactly as the server does — the canonical payload hash the
    /// request must claim. The server rejects a lying id with 400, so the quickstart's hashes must
    /// be honest; this helper computes them from the same fields the payload carries.
    /// </summary>
    private static string ChangesetHash(
        string message,
        IReadOnlyList<FileChange> changes,
        IReadOnlyList<string>? parents,
        long timestampUtc,
        string authorName,
        string authorEmail)
    {
        var entries = changes.Select(c => new ChangeEntry(
            c.Path,
            c.OldContent is null ? null : Sha256.HashHex(c.OldContent),
            c.Content is null ? null : Sha256.HashHex(c.Content),
            Scion.Core.FileContentKind.Regular,
            c.Content is null ? ChangeType.Deleted : c.OldContent is null ? ChangeType.Added : ChangeType.Modified));
        var changeset = Changeset.Create([.. parents ?? []], [.. entries], message, new Author(authorName, authorEmail), timestampUtc);
        return changeset.Id;
    }
}

/// <summary>One file the agent changed: the previous content (for modify/delete) and the new content (for add/modify).</summary>
public sealed record FileChange(string Path, byte[]? OldContent, byte[]? Content)
{
    public static FileChange Add(string path, string content) => new(path, null, Encoding.UTF8.GetBytes(content));
    public static FileChange Modify(string path, string oldContent, string content) =>
        new(path, Encoding.UTF8.GetBytes(oldContent), Encoding.UTF8.GetBytes(content));
    public static FileChange Delete(string path, string oldContent) => new(path, Encoding.UTF8.GetBytes(oldContent), null);
}

/// <summary>
/// Reader for the /rpc/blobs/missing response format exactly as docs/agent-api.md specifies it:
/// one entry per requested hash in request order — 64 ASCII bytes of lowercase-hex hash key, then
/// length-prefixed chunks (8-byte unsigned little-endian length plus that many content bytes),
/// closed by a zero-length chunk. Parsing failures mean the wire format drifted from the document.
/// </summary>
public static class BlobStreamFraming
{
    public static List<(string Key, byte[] Content)> Parse(byte[] stream)
    {
        List<(string, byte[])> entries = [];
        var offset = 0;

        while (offset < stream.Length)
        {
            var key = Encoding.ASCII.GetString(stream, offset, 64);
            AssertKeyShape(key, offset);
            offset += 64;

            using var content = new MemoryStream();
            while (true)
            {
                var length = BitConverter.ToUInt64(stream, offset);
                offset += sizeof(ulong);
                if (length == 0)
                {
                    break;
                }

                content.Write(stream, offset, (int)length);
                offset += (int)length;
            }

            entries.Add((key, content.ToArray()));
        }

        return entries;
    }

    private static void AssertKeyShape(string key, int offset)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException($"Blob stream entry at offset {offset} does not open with a 64-hex hash key: '{key}'.");
        }
    }
}
