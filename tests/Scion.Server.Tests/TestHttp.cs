using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scion.Server.Tests;

/// <summary>
/// The JSON options the documented wire format uses: camelCase property names and camelCase enum
/// names — exactly the server's Http.Json configuration. Tests serialize requests through this so
/// their payloads are byte-for-byte the shape docs/agent-api.md tells agents to send.
/// </summary>
internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

internal static class TestHttp
{
    /// <summary>Reads and clones the response's JSON body into an inspectable element.</summary>
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Expected a JSON body from {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} " +
                $"({(int)response.StatusCode}) but could not parse it: '{Truncate(body)}'.");
        }
    }

    /// <summary>The stable machine-readable error code every problem body carries.</summary>
    public static string Code(this JsonElement problem)
    {
        return problem.GetProperty("code").GetString()
            ?? throw new InvalidOperationException($"Problem body carries no 'code' string: {problem}.");
    }

    private static string Truncate(string body) => body.Length <= 200 ? body : body[..200] + "…";
}
