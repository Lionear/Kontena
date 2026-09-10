namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// Something to put on a cluster the moment it answers, named by a provisioner that has just made one
/// (KON-465) — the CNI a kind cluster was told not to install itself, and whatever else is chosen at
/// create time later on.
/// <para>
/// A reference rather than the YAML. Resolving one means fetching a URL or rendering a chart, and an
/// adapter that shells out to a single tool has no reason to grow an HTTP client and a Helm renderer
/// alongside it — so it says what it wants and the create flow gets it.
/// </para>
/// </summary>
/// <param name="DisplayName">What to call it while it is being applied, version included: "Calico v3.32.2".</param>
/// <param name="Url">
/// Where it comes from: a manifest to fetch, or — with <see cref="HelmRelease"/> set — a chart for
/// <c>helm template</c> to render.
/// </param>
public sealed record ClusterManifest(string DisplayName, string Url)
{
    /// <summary>
    /// Set when <see cref="Url"/> is a Helm chart rather than plain YAML, to the release name templates
    /// render into resource names. Not an exotic case: Cilium publishes no install manifest at all, only
    /// a chart, and it is not going to be the last project to make that choice.
    /// </summary>
    public string? HelmRelease { get; init; }

    /// <summary>Values for the render, as <c>key=value</c>. Only meaningful with <see cref="HelmRelease"/>.</summary>
    public IReadOnlyList<string> HelmValues { get; init; } = [];

    /// <summary>
    /// Where documents that name no namespace of their own go. A rendered chart usually names none —
    /// <c>helm template --namespace</c> tells templates without writing it into the output.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;
}
