using Kontena.Sdk;

namespace Kontena.Sdk;

/// <summary>
/// Describes and creates a backend. This is the extensibility seam shared by both axes of
/// Kontena: container engines today (Docker, Podman — CEAL) and cluster orchestrators later
/// (Kubernetes — OAL), plus future store-installed adapters. The host's backend registry
/// discovers providers and probes them for availability.
/// <para>
/// Today every provider yields an <see cref="IContainerEngine"/>; when the OAL lands, cluster
/// providers will yield an <c>IClusterEngine</c> instead, and one provider may surface several
/// context entries (e.g. kubeconfig contexts). The switcher groups them (Engines / Clusters).
/// </para>
/// </summary>
public interface IBackendProvider
{
    /// <summary>Stable backend id, e.g. "docker", "podman", "kubernetes:prod-eu-west".</summary>
    string Backend { get; }

    /// <summary>Human-facing name shown in the switcher, e.g. "Docker" or a kube-context name.</summary>
    string DisplayName { get; }

    /// <summary>Single-letter/short chip for the switcher, e.g. "D", "P", "K8s".</summary>
    string Chip { get; }

    /// <summary>
    /// The mark and colour for this backend's chip, or null to fall back to <see cref="Chip"/> as a
    /// letter badge (KON-80). Null is the default so an adapter — including a third-party one — is not
    /// forced to draw a logo it does not have; the demo backends use it deliberately.
    /// </summary>
    BackendChipStyle? ChipStyle => null;

    /// <summary>Which axis this backend belongs to — drives switcher grouping and UI mode.</summary>
    BackendKind Kind { get; }

    /// <summary>
    /// Whether there is any sign of this backend on this machine — a socket, a binary, an environment
    /// variable pointing at one. It is not "is it running": an engine that is installed but stopped
    /// must still answer true, because "it is here, it is not running" is exactly what someone opens
    /// the switcher to find out (KON-255).
    /// <para>
    /// True by default, which means "always listed". Everything a user added themselves — a remote, a
    /// kube-context, a plugin's backends — is there because they asked for it, and asking a provider
    /// to prove it exists before showing it would be the wrong question. The two engines the catalog
    /// offers unasked are the ones that answer this honestly: on a machine with only Docker, Podman
    /// used to sit in the switcher as an unclickable "Not connected" row forever.
    /// </para>
    /// <para>
    /// Cheap and local: no daemon start, no network. It is read while building a list a user is
    /// waiting on, and its answer only decides whether a row is worth drawing.
    /// </para>
    /// </summary>
    bool IsInstalled => true;

    /// <summary>
    /// How long this provider gets to answer a probe before it counts as unreachable.
    /// <para>
    /// Two seconds by default, which is a local socket's budget: a probe round sits between the user
    /// and their Settings page and costs whatever its slowest provider costs, the catalog always offers
    /// Docker and Podman whether installed or not, and connecting to a Windows named pipe that does not
    /// exist takes seconds to give up (KON-317, found via KON-306). An engine that is running answers in
    /// milliseconds, so that deadline only ever truncates a wait whose answer was going to be "no".
    /// </para>
    /// <para>
    /// It is per provider because a remote does not fit that shape at all: TCP, key exchange and auth to
    /// a host across a WAN routinely cost more than two seconds, and a provider cut off at a deadline it
    /// cannot make is unreachable by construction — Settings would say "Connected" about the very host
    /// the switcher calls dead (KON-327). Raise it only for what genuinely crosses a network; every
    /// second here is a second the whole round can take.
    /// </para>
    /// </summary>
    TimeSpan ProbeTimeout => TimeSpan.FromSeconds(2);

    /// <summary>Create a fresh backend instance — an <see cref="IContainerEngine"/> for
    /// <see cref="BackendKind.Engine"/>, an <c>IClusterEngine</c> for <see cref="BackendKind.Cluster"/>.</summary>
    IBackend CreateBackend();
}
