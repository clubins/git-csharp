using Microsoft.Extensions.Options;
using Scion.Core.Derived;
using Scion.Core.Storage;
using Scion.Server.Endpoints;
using Scion.Server.Storage;

namespace Scion.Server.Derived;

/// <summary>
/// Registration for the derived-data layer: the derived-value store (beside the changeset log in
/// the shared database), the engine over it with every v1 derived type registered, the boot sweep
/// that materializes eager types before the first request, and the read endpoint module that
/// queries through the engine. Any endpoint needing derived values — a fetch module walking trees,
/// a merge-base query over the skip graph — resolves the same engine singleton: one memo, one
/// store, one registry of types.
/// </summary>
public static class ScionDerivedDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the derived-data store, engine, boot sweep, and read endpoints. Composes with
    /// <see cref="ScionStorageServiceCollectionExtensions.AddScionStorage"/>, which registers the
    /// changeset log the engine replays from and the options binding that names the data directory.
    /// </summary>
    public static IServiceCollection AddScionDerivedData(this IServiceCollection services)
    {
        services.AddSingleton<DerivedDataStore>(sp => new DerivedDataStore(DatabasePath(sp)));
        services.AddSingleton<DerivedDataEngine>(sp => new DerivedDataEngine(
            sp.GetRequiredService<ChangesetLog>(),
            sp.GetRequiredService<DerivedDataStore>(),
            new TreeSnapshotDerivedData(),
            new SkipGraphDerivedData(),
            new PathHistoryDerivedData()));

        services.AddHostedService<DerivedDataMaterializationService>();
        services.AddSingleton<IScionEndpoints, ReadEndpoints>();
        // History operations: merge-base queries and the server-side merge / rebase that produce
        // changesets — they read the derived trees, so they register beside the engine's read module.
        services.AddSingleton<IScionEndpoints, QueryMergeEndpoints>();
        return services;
    }

    /// <summary>The changeset log's own database file — the derived-value store's conventional home.</summary>
    private static string DatabasePath(IServiceProvider serviceProvider)
    {
        var dataDirectory = serviceProvider.GetRequiredService<IOptions<ScionStorageOptions>>().Value.DataDirectory;
        return Path.Combine(dataDirectory, ScionStorageServiceCollectionExtensions.DatabaseFileName);
    }
}
