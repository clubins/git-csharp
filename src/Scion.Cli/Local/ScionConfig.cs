using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scion.Cli.Local;

/// <summary>
/// Per-repository connection settings, written by <c>scn init</c> and read by every command that
/// talks to the server. The environment wins over the file (see <see cref="ScionEnvironment"/>),
/// so the token can live outside the working copy.
/// </summary>
internal sealed record ScionConfig(string Server, string? Token = null, string? AuthorName = null, string? AuthorEmail = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ScionConfig Load(string repoRoot)
    {
        var path = ScionPaths.ConfigPath(repoRoot);
        try
        {
            return JsonSerializer.Deserialize<ScionConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new ScionCliException($"Local config '{path}' is empty; run 'scn init' again.");
        }
        catch (FileNotFoundException)
        {
            throw new ScionCliException($"No local config at '{path}' — not a scion repository (run 'scn init' first).");
        }
        catch (JsonException failure)
        {
            throw new ScionCliException($"Local config '{path}' is not valid JSON: {failure.Message}");
        }
    }

    public void Save(string repoRoot)
    {
        Directory.CreateDirectory(ScionPaths.StateDirectory(repoRoot));
        File.WriteAllText(ScionPaths.ConfigPath(repoRoot), JsonSerializer.Serialize(this, JsonOptions));
    }
}
