using System.CommandLine;

namespace Scion.Cli;

/// <summary>
/// The <c>scn</c> entry point: the root command with its global <c>-C</c> option (run as if
/// started in this directory, like git's), the thin client's commands, and the
/// failure mapping — parse errors are printed by the framework, command failures surface as
/// one-line messages on stderr with exit code 1.
/// </summary>
internal static class Program
{
    private static readonly Option<string?> WorkingDirectoryOption = new("-C")
    {
        Description = "Run as if scn was started in this directory instead of the current one",
    };

    public static int Main(string[] args) => Run(args, new CliContext(Console.Out, Console.Error));

    /// <summary>Parses and invokes the root command against a context — the seam tests use to drive the real pipeline with captured output and a test-server handler.</summary>
    public static int Run(string[] args, CliContext context)
    {
        try
        {
            return Build(context).Parse(args).Invoke();
        }
        catch (Exception exception) when (exception is ScionCliException or ScionServerException)
        {
            context.Err.WriteLine($"scn: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            context.Err.WriteLine($"scn: unexpected failure: {exception.Message}");
            return 1;
        }
    }

    internal static RootCommand Build(CliContext context)
    {
        var rootCommand = new RootCommand("scn - the thin Scion client. Never clones; the server answers what git makes every client compute.")
        {
            WorkingDirectoryOption,
            Commands.InitCommand.Build(context, WorkingDirectoryOption),
            Commands.StatusCommand.Build(context, WorkingDirectoryOption),
            Commands.CommitCommand.Build(context, WorkingDirectoryOption),
            Commands.LogCommand.Build(context, WorkingDirectoryOption),
            Commands.DiffCommand.Build(context, WorkingDirectoryOption),
        };
        return rootCommand;
    }
}
