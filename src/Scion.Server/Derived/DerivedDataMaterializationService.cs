using Scion.Core.Derived;

namespace Scion.Server.Derived;

/// <summary>
/// The derived-data engine's boot sweep as a hosted service: at host start, every eager derived
/// type (tree snapshots, path history) is materialized across the whole log before the first
/// request, so hot-path queries never pay first-query computation. The sweep is incremental —
/// stored values are skipped — and best-effort at the edges; a log it cannot read at all fails
/// startup, matching the server's fail-closed boot.
/// </summary>
internal sealed class DerivedDataMaterializationService(DerivedDataEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.MaterializeEager();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
