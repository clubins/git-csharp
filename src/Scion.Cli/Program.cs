namespace Scion.Cli;

// Thin `scn` client. Commands (init, commit, log, diff, checkout) arrive in later PRs -
// this scaffold exists so the project layout, build, and test wiring are proven end to end.
internal static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("scn: thin client for the Scion server (scaffold - no commands yet)");
        return 0;
    }
}
