using System.Net.Http.Json;
using System.Text.Json;
using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// The e2e harness: the real <see cref="Program"/> pipeline driven in-process against the real
/// server under the shared <see cref="ScionServerFactory"/> (TestServer handler), with a
/// throwaway working directory per instance. <see cref="RunCli"/> always passes <c>-C</c>, so
/// tests never depend on process cwd.
/// </summary>
public sealed class CliHarness : IDisposable
{
    public ScionServerFactory Factory { get; } = new();

    public string RepoRoot { get; } = Path.Combine(Path.GetTempPath(), "scn-cli-tests", Guid.NewGuid().ToString("N"));

    public CliHarness() => Directory.CreateDirectory(RepoRoot);

    /// <summary>The server URL as the CLI sees it — the TestServer answers on any host.</summary>
    public string ServerUrl => Factory.ClientOptions.BaseAddress?.ToString() ?? throw new InvalidOperationException("The test server has no base address.");

    /// <summary>Runs the real parse-and-invoke pipeline with both writers captured.</summary>
    public (int Exit, string Output) RunCli(params string[] args)
    {
        var writer = new StringWriter();
        var exit = Program.Run(["-C", RepoRoot, .. args], new CliContext(writer, writer, Factory.Server.CreateHandler()));
        return (exit, writer.ToString());
    }

    /// <summary>A GET against the test server with the harness's token, parsed as JSON.</summary>
    public async Task<JsonElement> GetJsonAsync(string requestUri)
    {
        using var client = Factory.CreateAuthedClient();
        using var response = await client.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose()
    {
        Factory.Dispose();
        if (Directory.Exists(RepoRoot))
        {
            Directory.Delete(RepoRoot, recursive: true);
        }
    }
}
