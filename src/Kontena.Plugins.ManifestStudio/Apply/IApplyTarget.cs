using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Apply;

/// <summary>
/// The one thing <see cref="PlanApplyViewModel"/> needs from a cluster, carved out of
/// <see cref="IClusterEngine"/> for the same reason <c>IClusterSchemaSource</c> is (KON-288): a test
/// double for one method, not the whole OAL surface.
/// </summary>
public interface IApplyTarget
{
    IAsyncEnumerable<ApplyProgress> ApplyAsync(ManifestBundle bundle, CancellationToken ct = default);
}

/// <summary>Adapts any real <see cref="IClusterEngine"/> the host hands the plugin — plan and apply are
/// pure reuse (Plan §1): KON-69/86 already built <c>ApplyAsync</c>, this plugin does not reimplement it.</summary>
public sealed class ClusterEngineApplyTarget(IClusterEngine engine) : IApplyTarget
{
    public IAsyncEnumerable<ApplyProgress> ApplyAsync(ManifestBundle bundle, CancellationToken ct = default) =>
        engine.ApplyAsync(bundle, ct: ct);
}
