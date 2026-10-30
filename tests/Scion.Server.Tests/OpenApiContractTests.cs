using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scion.Server.Errors;

namespace Scion.Server.Tests;

/// <summary>
/// The document-vs-server contract (spec D5): the OpenAPI document served at
/// <c>/openapi/v1.json</c> is generated from the same route table the server serves, and these
/// tests hold that generation honest on the three things an agent client depends on — the
/// operation set (every production route documented, nothing else), the status contract per
/// operation (C7's 400/404/409 vocabulary, the 200/201 successes, the 401 the bearer gate adds),
/// and the enum spelling the wire carries (camelCase, matching the JSON layer's converter).
/// The behavioral half of the contract is <see cref="AgentApiParityTests"/>.
/// </summary>
public sealed class OpenApiContractTests : IDisposable
{
    private readonly ScionServerFactory factory;

    public OpenApiContractTests()
    {
        factory = new ScionServerFactory();
    }

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task Document_IsAvailableAnonymously_AsOpenApiJson()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var document = await response.JsonAsync();
        Assert.True(document.GetProperty("openapi").GetString()!.StartsWith("3.1", StringComparison.Ordinal),
            $"The document must declare an OpenAPI 3.1 version, got '{document.GetProperty("openapi").GetString()}'.");
    }

    [Fact]
    public async Task Document_CoversExactlyTheProductionRouteTable()
    {
        var (documented, fromRoutes) = await DocumentedAndRoutedOperationsAsync();

        var missing = fromRoutes.Except(documented).OrderBy(op => op).ToList();
        var spurious = documented.Except(fromRoutes).OrderBy(op => op).ToList();
        Assert.True(missing.Count == 0 && spurious.Count == 0,
            $"Document and route table disagree. Documented but not routed: [{string.Join(", ", spurious)}]. " +
            $"Routed but not documented: [{string.Join(", ", missing)}].");
    }

    /// <summary>
    /// The status contract per operation, exactly as docs/agent-api.md's endpoint reference
    /// documents it. A change to any endpoint's Produces metadata that is not mirrored here —
    /// or a documented status the endpoint cannot answer — fails the build.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> StatusContract = new Dictionary<string, string[]>
    {
        ["GET /healthz"] = ["200"],
        ["POST /rpc/blobs/{hash}"] = ["200", "201", "400", "401"],
        ["POST /rpc/changesets"] = ["201", "400", "409", "401"],
        ["GET /rpc/changesets/{id}"] = ["200", "404", "401"],
        ["GET /rpc/log"] = ["200", "400", "404", "401"],
        ["GET /rpc/path-history"] = ["200", "400", "404", "401"],
        ["GET /rpc/diff"] = ["200", "400", "404", "401"],
        ["GET /rpc/blame"] = ["200", "400", "404", "401"],
        ["GET /rpc/merge-base"] = ["200", "400", "404", "401"],
        ["POST /rpc/merge"] = ["201", "400", "404", "409", "401"],
        ["POST /rpc/rebase"] = ["201", "400", "404", "401"],
        ["POST /rpc/trees/missing"] = ["200", "400", "404", "401"],
        ["POST /rpc/blobs/missing"] = ["200", "400", "404", "401"],
    };

    [Fact]
    public async Task EveryOperation_DeclaresTheDocumentedStatusContract()
    {
        var document = await DocumentAsync();

        foreach (var (operationId, expected) in StatusContract)
        {
            var responses = ResponsesOf(document, operationId);
            var actual = responses.EnumerateObject().Select(p => p.Name).OrderBy(code => code, StringComparer.Ordinal).ToArray();
            var expectedSorted = expected.OrderBy(code => code, StringComparer.Ordinal).ToArray();

            Assert.True(actual.SequenceEqual(expectedSorted),
                $"{operationId}: the document advertises [{string.Join(", ", actual)}] but the contract is [{string.Join(", ", expectedSorted)}].");
        }
    }

    [Fact]
    public async Task EveryAuthenticatedOperation_DeclaresBearerSecurity_AndThe401Problem()
    {
        var document = await DocumentAsync();

        foreach (var pathProperty in document.GetProperty("paths").EnumerateObject())
        {
            var path = pathProperty.Name;
            foreach (var operation in pathProperty.Value.EnumerateObject())
            {
                var operationId = $"{operation.Name.ToUpperInvariant()} {path}";
                var anonymous = path == "/healthz";

                var declaresBearer = operation.Value.TryGetProperty("security", out var security)
                    && security.EnumerateArray().Any(requirement =>
                        requirement.EnumerateObject().Any(scheme => scheme.Name == ScionOpenApi.BearerSchemeId));
                var declares401 = operation.Value.TryGetProperty("responses", out var responses)
                    && responses.TryGetProperty("401", out _);

                Assert.True(anonymous || declaresBearer,
                    $"{operationId}: authenticated operations must declare the '{ScionOpenApi.BearerSchemeId}' security requirement.");
                Assert.True(!anonymous || !declaresBearer,
                    $"{operationId}: anonymous operations must not declare a security requirement.");
                Assert.Equal(!anonymous, declares401);
            }
        }
    }

    /// <summary>
    /// The enum schemas must advertise the camelCase values the wire carries — the behavioral
    /// parity tests pin what responses actually serialize; this pins that the document cannot
    /// regress to PascalCase or numeric values.
    /// </summary>
    [Fact]
    public async Task EnumSchemas_AdvertiseTheLowercaseValuesTheWireCarries()
    {
        var document = await DocumentAsync();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // ChangeType
            "added", "modified", "deleted", "renamed",
            // FileContentKind
            "regular", "executable", "symlink",
            // MergeConflictKind
            "content", "deleteModify", "renameRename", "renameDelete",
        };

        var violations = new List<string>();
        CollectEnumViolations(document, path: "$", allowed, violations);

        Assert.True(violations.Count == 0,
            "Enum schemas advertise values the wire never sends: " + string.Join("; ", violations));
    }

    [Fact]
    public async Task ErrorResponses_DescribeAJsonProblemBody()
    {
        var document = await DocumentAsync();

        foreach (var pathProperty in document.GetProperty("paths").EnumerateObject())
        {
            var path = pathProperty.Name;
            foreach (var operation in pathProperty.Value.EnumerateObject())
            {
                foreach (var responseProperty in operation.Value.GetProperty("responses").EnumerateObject())
                {
                    var code = responseProperty.Name;
                    var response = responseProperty.Value;

                    if (code is not ("400" or "401" or "404" or "409"))
                    {
                        continue;
                    }

                    var hasJsonSchema = response.TryGetProperty("content", out var content)
                        && content.TryGetProperty("application/json", out var json)
                        && json.TryGetProperty("schema", out _);

                    Assert.True(hasJsonSchema, $"{operation.Name.ToUpperInvariant()} {path}: response {code} must describe its JSON problem body.");
                }
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both halves of the operation set: what the document advertises (paths × methods) and what
    /// the live route table serves (every RouteEndpoint under /rpc plus /healthz — the OpenAPI
    /// route and the test factory's /test probes are not production surface).
    /// </summary>
    private async Task<(HashSet<string> Documented, HashSet<string> Routed)> DocumentedAndRoutedOperationsAsync()
    {
        var document = await DocumentAsync();
        var documented = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pathProperty in document.GetProperty("paths").EnumerateObject())
        {
            var path = pathProperty.Name;
            foreach (var method in pathProperty.Value.EnumerateObject())
            {
                documented.Add($"{method.Name.ToUpperInvariant()} {path}");
            }
        }

        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var routed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            // RawText carries the leading slash ('/rpc/log'), matching the document's path keys.
            var pattern = endpoint.RoutePattern.RawText;
            if (pattern is null || !(pattern.StartsWith("/rpc/", StringComparison.Ordinal) || pattern == "/healthz"))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.OfType<IHttpMethodMetadata>().SelectMany(m => m.HttpMethods))
            {
                routed.Add($"{method.ToUpperInvariant()} {pattern}");
            }
        }

        return (documented, routed);
    }

    private async Task<JsonElement> DocumentAsync()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static JsonElement ResponsesOf(JsonElement document, string operationId)
    {
        var space = operationId.IndexOf(' ');
        var method = operationId[..space].ToLowerInvariant();
        var path = operationId[(space + 1)..];
        return document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");
    }

    private static void CollectEnumViolations(JsonElement element, string path, ISet<string> allowed, List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        var name = value.GetString();
                        if (name is null || !allowed.Contains(name))
                        {
                            violations.Add($"{path}: enum value '{name}' is not a camelCase member of the documented vocabulary.");
                        }
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectEnumViolations(property.Value, $"{path}.{property.Name}", allowed, violations);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectEnumViolations(item, $"{path}[]", allowed, violations);
                }

                break;
        }
    }
}
