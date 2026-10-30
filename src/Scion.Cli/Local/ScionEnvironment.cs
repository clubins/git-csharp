namespace Scion.Cli.Local;

/// <summary>
/// Environment overrides over the local config file. The environment wins so a token or author
/// can live outside the working copy (CI, shared checkouts); the config file carries the defaults
/// <c>scn init</c> recorded.
/// </summary>
internal static class ScionEnvironment
{
    public const string ServerVariable = "SCN_SERVER";
    public const string TokenVariable = "SCN_TOKEN";
    public const string AuthorNameVariable = "SCN_AUTHOR_NAME";
    public const string AuthorEmailVariable = "SCN_AUTHOR_EMAIL";

    public static string Server(ScionConfig config) =>
        First(Environment.GetEnvironmentVariable(ServerVariable), config.Server)!;

    public static string? Token(ScionConfig config) =>
        First(Environment.GetEnvironmentVariable(TokenVariable), config.Token);

    public static string? AuthorName(ScionConfig config) =>
        First(Environment.GetEnvironmentVariable(AuthorNameVariable), config.AuthorName);

    public static string? AuthorEmail(ScionConfig config) =>
        First(Environment.GetEnvironmentVariable(AuthorEmailVariable), config.AuthorEmail);

    /// <summary>The first non-empty value, or null when both are unset or blank.</summary>
    internal static string? First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : (string.IsNullOrWhiteSpace(fallback) ? null : fallback);
}
