using Microsoft.Extensions.Options;
using Scion.Core.Storage;
using Scion.Server.Endpoints;

namespace Scion.Server.Storage;

/// <summary>Registration for the server's three stores and the write-path endpoints that use them.</summary>
public static class ScionStorageServiceCollectionExtensions
{
    /// <summary>The shared SQLite database file: changeset log and ref store each own their tables in it.</summary>
    internal const string DatabaseFileName = "scion.db";

    /// <summary>The blob store's root directory, beside the database.</summary>
    private const string BlobDirectoryName = "blobs";

    /// <summary>
    /// Registers the three stores — the changeset log and the ref store against one SQLite
    /// database, the blob store against a hash-sharded directory — all rooted at
    /// <see cref="ScionStorageOptions.DataDirectory"/>, plus the write-path endpoint modules.
    /// Store constructors run the boot path (schema creation and registry validation), so a
    /// broken data directory fails startup rather than the first request.
    /// </summary>
    public static IServiceCollection AddScionStorage(this IServiceCollection services)
    {
        services.AddOptions<ScionStorageOptions>()
            .BindConfiguration(ScionStorageOptions.SectionName)
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.DataDirectory),
                "Scion:DataDirectory must be configured: the server refuses to start without a data directory.")
            .ValidateOnStart();

        services.AddSingleton<ChangesetLog>(sp => new ChangesetLog(DatabasePath(sp)));
        services.AddSingleton<RefStore>(sp => new RefStore(DatabasePath(sp)));
        services.AddSingleton<BlobStore>(sp => new BlobStore(BlobRootPath(sp)));

        services.AddSingleton<IScionEndpoints, ChangesetEndpoints>();
        services.AddSingleton<IScionEndpoints, BlobEndpoints>();
        return services;
    }

    private static string DataDirectory(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<IOptions<ScionStorageOptions>>().Value.DataDirectory;
    }

    private static string DatabasePath(IServiceProvider serviceProvider)
    {
        return Path.Combine(DataDirectory(serviceProvider), DatabaseFileName);
    }

    private static string BlobRootPath(IServiceProvider serviceProvider)
    {
        return Path.Combine(DataDirectory(serviceProvider), BlobDirectoryName);
    }
}
