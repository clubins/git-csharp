namespace Scion.Server.Storage;

/// <summary>
/// Where the server's stores live: one directory holding the SQLite database (the changeset log
/// and the ref store share the file) and the content-addressed blob root beside it. Configure via
/// the `Scion:DataDirectory` configuration key (env: `Scion__DataDirectory`); the server refuses
/// to start without one — a half-configured host that persists nowhere is worse than a failed boot.
/// </summary>
public sealed class ScionStorageOptions
{
    /// <summary>The configuration section that binds to these options.</summary>
    public const string SectionName = "Scion";

    /// <summary>Root directory for the stores; created on demand by the store constructors.</summary>
    public string DataDirectory { get; set; } = string.Empty;
}
