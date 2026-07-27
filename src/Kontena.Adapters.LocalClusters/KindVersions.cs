using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// The Kubernetes versions kind is offered for (KON-144).
/// <para>
/// A maintained list, and deliberately so: kind boots a prebuilt <c>kindest/node</c> image, those are
/// published per kind release, and there is nothing to enumerate without reaching out to a registry.
/// <c>kindest/node:latest</c> does not exist. So this is the one place in the create form that cannot
/// ask its tool — which is exactly why the node image field is offered next to it: anything not listed
/// here can still be typed in, and a stale list costs a convenience rather than a capability.
/// </para>
/// </summary>
public static class KindVersions
{
    /// <summary>
    /// The newest patch of each supported minor, newest first. Verified to exist on Docker Hub, and
    /// v1.36.1 was booted on kind v0.31.0 to check that an older kind does not choke on a node image
    /// published alongside a newer one (it does not).
    /// </summary>
    public static ClusterVersionOptions Options { get; } =
        new(["v1.36.1", "v1.35.5", "v1.34.8", "v1.33.12"]);
}
