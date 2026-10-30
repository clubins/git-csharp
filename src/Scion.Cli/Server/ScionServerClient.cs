using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scion.Cli.Server;

/// <summary>
/// The thin client's HTTP half: the three endpoints the commit path needs — blob upload, changeset
/// append, and tree discovery — plus the bearer token. All content is addressed by its SHA-256;
/// uploads are idempotent on the server (201 new / 200 duplicate), so a retry after a timeout is
/// always safe. Failures surface as <see cref="ScionServerException"/> carrying the server's
/// ProblemDetails detail when one came back.
/// </summary>
internal sealed class ScionServerClient(string serverUrl, string? token, HttpMessageHandler? handler = null) : IDisposable
{
    // The exact options the server serializes with: Web defaults plus camelCase string enums
    // (verified against ScionServerServiceCollectionExtensions), so both ends hash the same wire.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient client = CreateClient(serverUrl, token, handler);

    /// <summary>Stores one blob under its content address. Idempotent: existing content is a 200 no-op.</summary>
    public async Task UploadBlobAsync(string hash, byte[] content, CancellationToken cancellationToken = default)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync($"rpc/blobs/{hash}", body, cancellationToken);
        await EnsureSuccessAsync(response, "blob upload", cancellationToken);
    }

    /// <summary>
    /// Appends the changeset to the server's write-ahead log; the server re-derives the id from
    /// the canonical payload and rejects any mismatch. Returns the stored changeset's id.
    /// </summary>
    public async Task<string> CreateChangesetAsync(CreateChangesetPayload changeset, CancellationToken cancellationToken = default)
    {
        using var body = JsonBody(changeset);
        using var response = await client.PostAsync("rpc/changesets", body, cancellationToken);
        await EnsureSuccessAsync(response, "changeset creation", cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<CreatedChangeset>(JsonOptions, cancellationToken);
        return created?.Id is { Length: 64 } id
            ? id
            : throw new ScionServerException(response.StatusCode, "The server's changeset response carried no id.");
    }

    /// <summary>
    /// Asks the server which of the tree's blob hashes it holds, walking the tree at
    /// <paramref name="changesetId"/> — the commit path's "diff against the remote tree" step.
    /// </summary>
    public async Task<TreesMissing> TreesMissingAsync(string changesetId, CancellationToken cancellationToken = default)
    {
        using var body = JsonBody(new TreesMissingRequest(changesetId));
        using var response = await client.PostAsync("rpc/trees/missing", body, cancellationToken);
        await EnsureSuccessAsync(response, "tree lookup", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TreesMissing>(JsonOptions, cancellationToken);
        return result ?? throw new ScionServerException(response.StatusCode, "The server's trees/missing response was empty.");
    }

    public void Dispose() => client.Dispose();

    private static HttpClient CreateClient(string serverUrl, string? token, HttpMessageHandler? handler)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new ScionCliException($"Server URL '{serverUrl}' is not an absolute http(s) URL.");
        }

        var httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Path).EndsWith('/')
                ? baseUri.GetLeftPart(UriPartial.Path)
                : baseUri.GetLeftPart(UriPartial.Path) + "/"),
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return httpClient;
    }

    private static StringContent JsonBody<T>(T payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ScionServerException(response.StatusCode, Describe(response.StatusCode, operation, raw));
    }

    private static string Describe(HttpStatusCode statusCode, string operation, string rawBody)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return $"{operation} failed: the server rejected the credentials (401). Check the token — set it with 'scn init --token' or the SCN_TOKEN environment variable.";
        }

        var problem = TryReadProblem(rawBody);
        if (problem?.Detail is { Length: > 0 } detail)
        {
            return $"{operation} failed ({(int)statusCode}): {detail}";
        }

        var suffix = rawBody.Length > 0 ? $": {Truncate(rawBody)}" : string.Empty;
        return $"{operation} failed ({(int)statusCode}){suffix}";
    }

    private static ProblemBody? TryReadProblem(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProblemBody>(rawBody, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";

    private sealed record TreesMissingRequest(string ChangesetId);

    private sealed record ProblemBody(string? Title, string? Detail);
}
