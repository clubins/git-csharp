using System.CommandLine;
using Scion.Cli.Local;
using Scion.Cli.Working;
using Scion.Core;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn status</c>: the working copy against the last-known manifest, answered entirely from
/// local state — the tip, the manifest, and a fresh scan. No server round trip, so status stays
/// O(change) and never contends with the server or other agents.
/// </summary>
internal static class StatusCommand
{
    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var command = new Command("status", "Show the tip and the working-copy changes, computed locally against the last-known manifest.");
        command.SetAction(parseResult => CliGuard.Run(
            () => Run(
                parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
                context),
            context));
        return command;
    }

    public static int Run(string workingDirectory, CliContext context)
    {
        var (root, config, state) = Repository.Open(workingDirectory);
        var changes = WorkingChangeSet.Diff(state.Files, WorkingFileScanner.Scan(root));

        context.Out.WriteLine($"scn repository at {root}");
        context.Out.WriteLine($"server: {ScionEnvironment.Server(config)}");
        context.Out.WriteLine($"tip: {state.Tip ?? "(none)"}");
        context.Out.WriteLine(string.Empty);

        if (changes.Count == 0)
        {
            context.Out.WriteLine("no changes");
            return 0;
        }

        context.Out.WriteLine("changes (relative to the last-known tip):");
        foreach (var change in changes)
        {
            context.Out.WriteLine($"  {Marker(change.Type)} {change.Path}");
        }

        context.Out.WriteLine(Counts(changes));
        return 0;
    }

    internal static string Counts(IReadOnlyList<WorkingChange> changes)
    {
        var (added, modified, deleted) = Count(changes);
        return $"{changes.Count} change(s): {added} added, {modified} modified, {deleted} deleted";
    }

    internal static (int Added, int Modified, int Deleted) Count(IReadOnlyList<WorkingChange> changes)
    {
        int added = 0, modified = 0, deleted = 0;
        foreach (var change in changes)
        {
            switch (change.Type)
            {
                case ChangeType.Added:
                    added++;
                    break;
                case ChangeType.Modified:
                    modified++;
                    break;
                case ChangeType.Deleted:
                    deleted++;
                    break;
            }
        }

        return (added, modified, deleted);
    }

    private static char Marker(ChangeType type) => type switch
    {
        ChangeType.Added => 'A',
        ChangeType.Modified => 'M',
        ChangeType.Deleted => 'D',
        ChangeType.Renamed => 'R',
        _ => '?',
    };
}
