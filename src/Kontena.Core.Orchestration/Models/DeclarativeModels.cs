namespace Kontena.Core.Orchestration.Models;

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

    /// <summary>Error message when <see cref="Action"/> is <see cref="ApplyAction.Failed"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>A Helm release, for the (stretch) Helm view. See KON-74.</summary>
public sealed record HelmRelease
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>Chart name and version, e.g. "ingress-nginx-4.10.0".</summary>
    public string Chart { get; init; } = string.Empty;

    /// <summary>App version the chart deploys.</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Release revision number.</summary>
    public int Revision { get; init; }

    /// <summary>Status, e.g. "deployed", "failed", "pending-upgrade".</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>When the release was last updated (UTC).</summary>
    public DateTimeOffset Updated { get; init; }
}
