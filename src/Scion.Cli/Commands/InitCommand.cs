using System.CommandLine;
using Scion.Cli.Local;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn init</c>: point a directory at a server. Writes the local config and an empty working
/// state — no network call, no clone; the working copy starts empty and content arrives by
/// request (the thin-client contract). Re-running init in a repository is refused, never merged.
/// </summary>
internal static class InitCommand
{
    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var serverOption = new Option<string>("--server", "-s")
        {
            Description = "The Scion server URL this repository points at",
            Required = true,
        };
        var tokenOption = new Option<string?>("--token", "-t")
        {
            Description = "The bearer token the server requires (the SCN_TOKEN environment variable wins at request time)",
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "The author name recorded on this client's commits (SCN_AUTHOR_NAME wins)",
        };
        var emailOption = new Option<string?>("--email")
        {
            Description = "The author email recorded on this client's commits (SCN_AUTHOR_EMAIL wins)",
        };

        var command = new Command("init", "Point a directory at a Scion server — no clone, no network call.")
        {
            serverOption,
            tokenOption,
            nameOption,
            emailOption,
        };

        command.SetAction(parseResult => CliGuard.Run(() => Run(
            parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
            parseResult.GetValue(serverOption)!, // Required = true: the parser refuses to invoke without it.
            parseResult.GetValue(tokenOption),
            parseResult.GetValue(nameOption),
            parseResult.GetValue(emailOption),
            context), context));

        return command;
    }

    public static int Run(string workingDirectory, string server, string? token, string? name, string? email, CliContext context)
    {
        if (Directory.Exists(ScionPaths.StateDirectory(workingDirectory)))
        {
            throw new ScionCliException($"'{workingDirectory}' is already a scion repository.");
        }

        var normalized = server.TrimEnd('/');
        new ScionConfig(normalized, token, name, email).Save(workingDirectory);
        new WorkingState().Save(workingDirectory);

        context.Out.WriteLine($"Initialized an empty scion repository at {workingDirectory}");
        context.Out.WriteLine($"Server: {normalized}");
        context.Out.WriteLine("No clone was made — the working copy starts empty; content is fetched by request.");
        return 0;
    }
}
