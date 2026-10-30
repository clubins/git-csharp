namespace Scion.Server.Auth;

/// <summary>
/// Bearer-token configuration. The token is required and the server refuses to start without
/// one (fail closed): an unauthenticated source-control server would expose the entire
/// repository history. Configure via the `Scion:AuthToken` configuration key (env:
/// `Scion__AuthToken`). v1 ships one token per repository — per-directory ACLs are deliberately
/// deferred (see spec cuts).
/// </summary>
public sealed class ScionAuthOptions
{
    /// <summary>The configuration section that binds to these options.</summary>
    public const string SectionName = "Scion";

    /// <summary>The shared bearer token; must be non-empty.</summary>
    public string Token { get; set; } = string.Empty;
}
