namespace Scion.Cli.Local;

/// <summary>
/// Locates the <c>.scn</c> directory that holds a repository's config and local state. Commands
/// operate on the repository root — the directory containing <c>.scn</c> — found by walking up
/// from the working directory, the way git walks up to <c>.git</c>.
/// </summary>
internal static class ScionPaths
{
    /// <summary>The per-repository directory holding config and local state.</summary>
    public const string DirectoryName = ".scn";

    /// <summary>
    /// The repository root — the nearest ancestor of <paramref name="startDirectory"/> containing
    /// <c>.scn</c> — or null when the working directory is outside any scion repository.
    /// </summary>
    public static string? FindRoot(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        while (true)
        {
            if (Directory.Exists(Path.Combine(current, DirectoryName)))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent;
        }
    }

    public static string StateDirectory(string repoRoot) => Path.Combine(repoRoot, DirectoryName);

    public static string ConfigPath(string repoRoot) => Path.Combine(StateDirectory(repoRoot), "config.json");

    public static string StatePath(string repoRoot) => Path.Combine(StateDirectory(repoRoot), "state.json");
}
