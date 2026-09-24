namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// A bundle of YAML documents to apply — the input to the declarative core, the neutral form
/// of <c>kubectl apply -f</c>. May hold multiple resources (a multi-doc YAML file).
/// </summary>
public sealed record ManifestBundle
{
    /// <summary>The raw YAML (one or more <c>---</c>-separated documents).</summary>
    public required string Yaml { get; init; }

    /// <summary>Optional source label (file name, "pasted", …) for progress/history.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Where documents that declare no namespace of their own should go. Rendered bundles usually
    /// have none — <c>helm template --namespace</c> tells templates the namespace without writing
    /// it into the output — so the caller says once, rather than every document repeating it.
    /// Empty falls back to the context's namespace, as <c>kubectl apply</c> does.
    /// </summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// When true, run server-side dry-run only (validate + diff, persist nothing) — the
    /// preview step before a real apply.
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>What an apply did to a single resource.</summary>
public enum ApplyAction
{
    /// <summary>The resource did not exist and was created.</summary>
    Created,

    /// <summary>The resource existed and changed.</summary>
    Configured,

    /// <summary>The resource existed and matched — no change.</summary>
    Unchanged,

    /// <summary>Dry-run: the resource does not exist yet and would be created.</summary>
    WouldCreate,

    /// <summary>Dry-run: the resource exists and would be updated; nothing was persisted.</summary>
    WouldChange,

    /// <summary>
    /// Dry-run: the resource could not be previewed because something the same bundle creates does
    /// not exist yet — the namespace it goes in, or the CRD that defines its kind. A real apply puts
    /// those first and this resource goes with the rest, so it is an ordering fact, not a rejection.
    /// </summary>
    Deferred,

    /// <summary>The resource failed to apply.</summary>
    Failed,
}

/// <summary>
/// One progress item streamed from <c>ApplyAsync</c> — reported per resource as the apply (or
/// dry-run) proceeds, so the UI can show a live per-object result list and a unified diff.
/// </summary>
public sealed record ApplyProgress
{
    public required ResourceRef Resource { get; init; }
    public required ApplyAction Action { get; init; }

    /// <summary>Unified diff for a changed resource (dry-run/diff flows); empty when none.</summary>
    public string Diff { get; init; } = string.Empty;

    /// <summary>
    /// Why, when <see cref="Action"/> is <see cref="ApplyAction.Failed"/> or
    /// <see cref="ApplyAction.Deferred"/>.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>A Helm release installed in the cluster, as <c>helm list</c> reports it (KON-473).</summary>
public sealed record HelmRelease
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>The chart's own name, without its version — <c>ingress-nginx</c>, not <c>ingress-nginx-4.10.1</c>.</summary>
    public string Chart { get; init; } = string.Empty;

    public string ChartVersion { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Helm's own word for it: deployed, failed, pending-upgrade, superseded, …</summary>
    public string Status { get; init; } = string.Empty;

    public int Revision { get; init; }

    /// <summary>When the current revision was written, or null when helm's timestamp did not parse.</summary>
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>One entry of a release's history, as <c>helm history</c> reports it.</summary>
public sealed record HelmRevision
{
    public required int Revision { get; init; }
    public string Status { get; init; } = string.Empty;

    /// <summary>Chart name and version as one string, the way helm writes it: <c>ingress-nginx-4.10.1</c>.</summary>
    public string Chart { get; init; } = string.Empty;

    public string AppVersion { get; init; } = string.Empty;

    /// <summary>What helm wrote about the revision: "Install complete", "Rollback to 2", …</summary>
    public string Description { get; init; } = string.Empty;

    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// An upgrade of an installed release: which chart to take it to, and the values it gets.
/// </summary>
public sealed record HelmUpgrade
{
    public required string Release { get; init; }
    public required string Namespace { get; init; }

    /// <summary>
    /// A chart reference helm can resolve — <c>repo/chart</c>, a path, or an <c>oci://</c> reference. Helm
    /// does not record where an installed chart came from, so this cannot be read back from the release.
    /// </summary>
    public required string Chart { get; init; }

    /// <summary>Chart version; empty takes the newest the repository offers.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// The user-supplied values as YAML. They replace the release's current ones — the same as
    /// <c>helm upgrade -f</c> without <c>--reuse-values</c>.
    /// </summary>
    public string ValuesYaml { get; init; } = string.Empty;
}
