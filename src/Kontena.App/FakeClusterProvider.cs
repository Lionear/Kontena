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
public sealed class FakeClusterProvider(string context, string chip) : IBackendProvider
{
    public string Backend => $"kubernetes:{context}";
    public string DisplayName => context;
    public string Chip => chip;
    public BackendKind Kind => BackendKind.Cluster;
    public IBackend CreateBackend() => new FakeClusterEngine(context);
}
