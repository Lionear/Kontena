using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Sdk.Orchestration;

/// <summary>
/// The Helm releases installed in a cluster, and what can be done to them (KON-473): read the list,
/// a release's values, manifest and history, and upgrade, roll back or uninstall one.
/// <para>
/// Every write returns null on success or helm's own complaint — a failed rollback is an answer the
/// user needs to read, not an exception to swallow.
/// </para>
/// </summary>
public interface IHelmReleases
{
    /// <summary>Releases in <paramref name="ns"/>, or in every namespace when null.</summary>
    ValueTask<IReadOnlyList<HelmRelease>> ListAsync(string? ns = null, CancellationToken ct = default);

    /// <summary>
    /// The release's values as YAML: only what the user supplied, or with <paramref name="all"/> the
    /// chart's defaults merged in — what the templates actually saw.
    /// </summary>
    ValueTask<string> GetValuesAsync(string release, string ns, bool all = false, CancellationToken ct = default);

    /// <summary>The manifests the current revision rendered.</summary>
    ValueTask<string> GetManifestAsync(string release, string ns, CancellationToken ct = default);

    /// <summary>Every revision helm still keeps, oldest first.</summary>
    ValueTask<IReadOnlyList<HelmRevision>> GetHistoryAsync(string release, string ns, CancellationToken ct = default);

    ValueTask<string?> UpgradeAsync(HelmUpgrade upgrade, CancellationToken ct = default);

    ValueTask<string?> RollbackAsync(string release, string ns, int revision, CancellationToken ct = default);

    ValueTask<string?> UninstallAsync(string release, string ns, CancellationToken ct = default);
}

/// <summary>
/// Implemented by cluster backends that can manage Helm releases. Optional, exactly like
/// <see cref="IAlertingAware"/>: <see cref="ClusterCapabilities.Helm"/> tells the UI whether to show
/// the page, and this is where the page gets its releases from.
/// </summary>
public interface IHelmAware
{
    IHelmReleases Helm { get; }
}
