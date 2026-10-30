using System.CommandLine;
using System.Text;
using Scion.Cli.Local;
using Scion.Cli.Server;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn log</c>: first-parent history, paged, answered by the server — the client names a tip
/// (or none, for the newest appended changeset) and renders the page it got. No local graph walk:
/// the server walks, the client prints (client–server asymmetry). A page that filled reports the
/// cursor that resumes it, so an agent keeps paging with <c>--after</c> — which always wins over
/// <c>--tip</c>, matching the server's own precedence.
/// </summary>
internal static class LogCommand
{
    private const int MaxLimit = 1_000;

    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var limitOption = new Option<int?>("--limit")
        {
            Description = $"The maximum number of changesets to print (1-{MaxLimit}; default: the server's page size)",
        };
        var tipOption = new Option<string?>("--tip")
        {
            Description = "The changeset to start from (default: the newest appended changeset)",
        };
        var afterOption = new Option<string?>("--after")
        {
            Description = "The resume cursor from the previous page: the walk starts at its first parent",
        };
        var onelineOption = new Option<bool>("--oneline")
        {
            Description = "One line per changeset: the id followed by the message's first line",
        };

        var command = new Command("log", "Print the first-parent history, paged — the server walks it, the client never does.")
        {
            limitOption,
            tipOption,
            afterOption,
            onelineOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => await CliGuard.RunAsync(() => RunAsync(
            parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(tipOption),
            parseResult.GetValue(afterOption),
            parseResult.GetValue(onelineOption),
            context,
            cancellationToken), context, cancellationToken));

        return command;
    }

    public static async Task<int> RunAsync(
        string workingDirectory,
        int? limit,
        string? tip,
        string? after,
        bool oneline,
        CliContext context,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new ScionCliException($"--limit must be an integer between 1 and {MaxLimit}.");
        }

        var (_, config, _) = Repository.Open(workingDirectory);
        using var client = new ScionServerClient(
            ScionEnvironment.Server(config),
            ScionEnvironment.Token(config),
            context.HttpHandler);

        var page = await client.LogAsync(tip, after, limit, cancellationToken);
        if (page.Changesets.Length == 0)
        {
            context.Out.WriteLine("no commits");
            return 0;
        }

        var entries = oneline
            ? page.Changesets.Select(OneLine)
            : page.Changesets.Select(FullRecord);
        context.Out.Write(string.Join("\n", entries) + "\n");

        // A filled page hands back the cursor that resumes it; the walk simply ended otherwise.
        if (page.NextAfter is { } cursor)
        {
            context.Out.WriteLine($"next page: scn log --after {cursor}");
        }

        return 0;
    }

    private static string OneLine(LogChangeset entry)
    {
        var firstLine = entry.Message.Split('\n', StringSplitOptions.TrimEntries)[0];
        return $"{entry.Id} {firstLine}";
    }

    private static string FullRecord(LogChangeset entry)
    {
        var builder = new StringBuilder();
        builder.Append("commit ").Append(entry.Id).Append('\n');
        foreach (var parent in entry.Parents)
        {
            builder.Append("parent ").Append(parent).Append('\n');
        }

        builder.Append("Author: ").Append(entry.Author.Name).Append(" <").Append(entry.Author.Email).Append(">\n");
        builder.Append("Date: ").Append(FormatTimestamp(entry.TimestampUtc)).Append('\n');
        builder.Append('\n');
        foreach (var line in entry.Message.Split('\n'))
        {
            builder.Append("    ").Append(line).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string FormatTimestamp(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
