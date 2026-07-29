using Kontena.Core;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines;

namespace Kontena.App;

/// <summary>
/// Provider for the in-memory <see cref="FakeClusterEngine"/> — the OAL counterpart of
/// <c>FakeEngineProvider</c>. Registered (behind the same dev-only gate as the fake engine)
/// once per seeded kube-context, so the grouped switcher shows a real "Clusters" section to
/// build the cluster UI against before <c>Kontena.Adapters.Kubernetes</c> exists. Lives in the
/// app, not in the OAL project, so the two backend axes never reference each other.
/// </summary>
public sealed class FakeClusterProvider(string context, string chip, BackendChipStyle? chipStyle = null)
    : IBackendProvider
{
    /// <summary>
    /// Its own id namespace, deliberately not "kubernetes": the real adapter registers one backend
    /// per kube-context under that prefix, and a user whose kubeconfig happens to hold a context
    /// named like a seeded one would otherwise collide with a demo backend.
    /// </summary>
    public const string FakeBackendPrefix = "fakecluster";

    public string Backend => $"{FakeBackendPrefix}:{context}";
    public string DisplayName => context;
    public string Chip => chip;

    /// <summary>
    /// Null in the app — a seeded cluster is not a real one and does not wear Kubernetes' helm (KON-80).
    /// The screenshot renderer passes one, because its shots stand in for real clusters.
    /// </summary>
    public BackendChipStyle? ChipStyle => chipStyle;
    public BackendKind Kind => BackendKind.Cluster;
    public IBackend CreateBackend() => new FakeClusterEngine(context);
}
