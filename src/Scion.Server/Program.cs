namespace Scion.Server;

// ASP.NET Core host for the source-control APIs. Endpoints (create-changeset, log, diff,
// blame, ref CAS, blob fetch) arrive in later PRs; this scaffold proves the wiring builds.
// Kept `partial` and public so WebApplicationFactory-based tests can reach Program later.
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/", () => "Scion server (scaffold)");

        app.Run();
    }
}
