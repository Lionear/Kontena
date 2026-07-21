using Kontena.Core.Models;

namespace Kontena.Core;

/// <summary>
/// The shared base of every backend Kontena talks to — the thin spine common to both
/// axes: container engines (the CEAL, <c>IContainerEngine</c>) and cluster orchestrators
/// (the OAL, <c>IClusterEngine</c>). It carries only what the shared chrome needs — a
/// stable id, identity/health, and a connectivity probe — so the switcher, title bar, and
/// <c>BackendRegistry</c> can treat any backend uniformly.
/// <para>
/// Capabilities are deliberately <b>not</b> on this base: an engine's capabilities and a
/// cluster's capabilities share nothing, so each axis exposes its own typed set. This keeps
/// the base honest — it models the genuine intersection, not a forced union.
/// </para>
/// </summary>
public interface IBackend
{
    /// <summary>Stable backend id, e.g. "docker", "podman", "kubernetes".</summary>
    string Backend { get; }

    /// <summary>Identity and current health of the backend.</summary>
    ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Lightweight connectivity check. Throws when unreachable.</summary>
    ValueTask PingAsync(CancellationToken ct = default);
}
