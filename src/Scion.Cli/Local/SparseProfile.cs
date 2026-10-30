using System.Text.Json;

namespace Scion.Cli.Local;

/// <summary>
/// The persisted sparse profile — the standing glob set a working copy materializes within.
/// Absent means no restriction: full materialization, the default. Present, it rides every
/// checkout as the fetch's server-side sparse filter — which can only narrow, never widen, what
/// a checkout's own wanted paths select — until a checkout widens or clears it, so a long-lived
/// agent working copy holds only its scope on disk and in cache.
/// </summary>
internal sealed record SparseProfileState
{
    /// <summary>The glob patterns, in the order last given.</summary>
    public List<string> Patterns { get; init; } = [];

    /// <summary>
    /// The persisted profile, or null when there is none — absence is the no-restriction default,
    /// not an error.
    /// </summary>
    public static SparseProfileState? Load(string repoRoot)
    {
        var path = ScionPaths.SparseProfilePath(repoRoot);

        // Absence is the no-restriction default, and the .scn directory may not even exist yet —
        // a missing path is a normal null, not an error.
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SparseProfileState>(File.ReadAllText(path), JsonOptions)
                ?? new SparseProfileState(); // A literal null file means no patterns: unrestricted.
        }
        catch (JsonException failure)
        {
            throw new ScionCliException($"Sparse profile '{path}' is not valid JSON: {failure.Message}");
        }
    }

    public void Save(string repoRoot)
    {
        Directory.CreateDirectory(ScionPaths.StateDirectory(repoRoot));
        File.WriteAllText(ScionPaths.SparseProfilePath(repoRoot), JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Removes the persisted profile — the full-materialization default returns.</summary>
    public static void Delete(string repoRoot)
    {
        var path = ScionPaths.SparseProfilePath(repoRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
