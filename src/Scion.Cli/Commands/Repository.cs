using Scion.Cli.Local;

namespace Scion.Cli.Commands;

/// <summary>Opens the repository a command operates on: root directory, config, and local state.</summary>
internal static class Repository
{
    public static (string Root, ScionConfig Config, WorkingState State) Open(string workingDirectory)
    {
        var root = ScionPaths.FindRoot(workingDirectory)
            ?? throw new ScionCliException($"'{workingDirectory}' is not a scion repository (run 'scn init' in the repository root).");
        return (root, ScionConfig.Load(root), WorkingState.Load(root));
    }
}
