namespace Kontena.Sdk.Tooling;

/// <summary>How ready a tool is to be used, as one value the UI can switch on.</summary>
public enum ToolState
{
    /// <summary>Nowhere on this machine.</summary>
    Missing,

    /// <summary>Present and new enough.</summary>
    Ready,

    /// <summary>Present, but older than <see cref="ExternalTool.MinimumVersion"/>.</summary>
    Outdated,

    /// <summary>On disk but it would not say what version it is — a broken install.</summary>
    Unusable,
}

/// <summary>
/// What Kontena knows about one tool right now: where it is, what version, and whether that is good
/// enough. Assembled by <see cref="ToolReadinessCheck"/> so the UI never has to combine these itself.
/// </summary>
/// <param name="Tool">The tool this is about.</param>
/// <param name="State">The single value a view switches on.</param>
/// <param name="Path">Where it was found, if anywhere.</param>
/// <param name="Version">What it answered when asked, if it answered.</param>
/// <param name="Managed">True when this is Kontena's own copy rather than a system install.</param>
/// <param name="Hint">The install (or upgrade) command to offer, if there is one for this machine.</param>
public sealed record ToolReadiness(
    ExternalTool Tool,
    ToolState State,
    string? Path,
    string? Version,
    bool Managed,
    InstallHint? Hint)
{
    /// <summary>Whether the tool can be used at all — outdated still counts.</summary>
    public bool Usable => State is ToolState.Ready or ToolState.Outdated;

    /// <summary>
    /// Whether Kontena could fetch this one itself. Needs a publisher that ships checksums, and an
    /// architecture they build for.
    /// </summary>
    public bool CanBeDownloaded => Tool.Release is not null && ToolPlatform.CanDownload;

    /// <summary>
    /// True when Kontena's copy was chosen to win over a system install (KON-153). Distinct from
    /// <see cref="Managed"/>, which only says which copy is in use: a managed copy is also what you get
    /// when there is no system install at all, and that is not a preference anyone expressed.
    /// </summary>
    public bool Preferred { get; init; }
}
