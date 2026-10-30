using System.Text.Json;
using System.Text.Json.Serialization;
using Scion.Core;

namespace Scion.Cli.Local;

/// <summary>
/// The thin client's local state: the last-known tip — the changeset the working copy's changes
/// are deltas against — and the manifest of every path the client last committed, with its blob
/// hash and content kind. No object database, no packs: one JSON file small enough to rewrite on
/// every commit, with all content addressed on the server.
/// </summary>
internal sealed record WorkingState
{
    /// <summary>The changeset the current working copy descends from; null until the first commit.</summary>
    public string? Tip { get; init; }

    /// <summary>The manifest: canonical (forward-slash) path to the content last committed there.</summary>
    public Dictionary<string, Entry> Files { get; init; } = [];

    /// <summary>One path's committed content: its blob hash and how the content was interpreted.</summary>
    public sealed record Entry(string Hash, FileContentKind Kind);

    public static WorkingState Load(string repoRoot)
    {
        var path = ScionPaths.StatePath(repoRoot);
        try
        {
            return JsonSerializer.Deserialize<WorkingState>(File.ReadAllText(path), JsonOptions)
                ?? throw new ScionCliException($"Local state '{path}' is empty; run 'scn init' again.");
        }
        catch (FileNotFoundException)
        {
            throw new ScionCliException($"No local state at '{path}' — not a scion repository (run 'scn init' first).");
        }
        catch (JsonException failure)
        {
            throw new ScionCliException($"Local state '{path}' is not valid JSON: {failure.Message}");
        }
    }

    public void Save(string repoRoot)
    {
        Directory.CreateDirectory(ScionPaths.StateDirectory(repoRoot));
        File.WriteAllText(ScionPaths.StatePath(repoRoot), JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
