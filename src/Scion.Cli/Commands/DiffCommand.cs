using System.CommandLine;
using Scion.Cli.Local;
using Scion.Cli.Server;
using Scion.Cli.Working;
using Scion.Core;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn diff</c>: with no arguments, the working copy against the last-known manifest — local
/// state only, no server round trip. With two changesets, the server's rename-aware delta between
/// their trees — no local tree materialization. Both modes print the same status-letter lines, so
/// a change renders identically whether it is still local or already committed. A rename (server
/// side only; the local manifest has no content to compare) prints the new path — the change
/// entry names where the file went.
/// </summary>
internal static class DiffCommand
{
    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var fromArgument = new Argument<string?>("from")
        {
            Description = "The changeset the diff starts from (give together with 'to'; omit both for the working copy)",
            // Nullable arguments still default to required arity: without this, 'scn diff' with
            // no arguments dies in the parser ("Required argument missing") before the action runs.
            Arity = ArgumentArity.ZeroOrOne,
        };
        var toArgument = new Argument<string?>("to")
        {
            Description = "The changeset the diff ends at",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("diff", "Print changes: the working copy by default, or the server-computed delta between two changesets.")
        {
            fromArgument,
            toArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) => await CliGuard.RunAsync(() => RunAsync(
            parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
            parseResult.GetValue(fromArgument),
            parseResult.GetValue(toArgument),
            context,
            cancellationToken), context, cancellationToken));

        return command;
    }

    public static async Task<int> RunAsync(
        string workingDirectory,
        string? from,
        string? to,
        CliContext context,
        CancellationToken cancellationToken)
    {
        if ((from is null) != (to is null))
        {
            throw new ScionCliException(
                "scn diff takes no arguments for the working copy, or exactly two changesets to diff between — not one.");
        }

        var (root, config, state) = Repository.Open(workingDirectory);

        if (from is not null && to is not null)
        {
            using var client = new ScionServerClient(
                ScionEnvironment.Server(config),
                ScionEnvironment.Token(config),
                context.HttpHandler);
            var page = await client.DiffAsync(from, to, cancellationToken);
            Print(context, page.Changes.Select(change => (change.Type, change.Path)));
            return 0;
        }

        var changes = WorkingChangeSet.Diff(state.Files, WorkingFileScanner.Scan(root));
        Print(context, changes.Select(change => (change.Type, change.Path)));
        return 0;
    }

    private static void Print(CliContext context, IEnumerable<(ChangeType Type, string Path)> changes)
    {
        var lines = changes.Select(change => $"{Marker(change.Type)} {change.Path}").ToList();
        if (lines.Count == 0)
        {
            context.Out.WriteLine("no changes");
            return;
        }

        foreach (var line in lines)
        {
            context.Out.WriteLine(line);
        }
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
