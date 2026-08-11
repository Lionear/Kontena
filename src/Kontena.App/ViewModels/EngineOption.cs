using System.Globalization;
using System.Windows.Input;
using Kontena.Core.Versioning;

namespace Kontena.App.ViewModels;

/// <summary>An engine entry shown in the backend-switcher dropdown.</summary>
public sealed class EngineOption
{
    public required string Backend { get; init; }

    public required string Name { get; init; }

    /// <summary>The backend's mark, or the letter to fall back to (KON-80).</summary>
    public required BackendChipInfo Chip { get; init; }

    /// <summary>Secondary line — version/endpoint, or a short "not connected" reason.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Whether this is the currently active engine.</summary>
    public bool IsActive { get; init; }

    /// <summary>Whether the backend answered a ping.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Whether this backend is being asked again right now (KON-328).</summary>
    public bool IsRetrying { get; init; }

    /// <summary>Says the click will re-probe rather than switch — a remote can take ten seconds to
    /// answer, and a row that looks inert for ten seconds reads as the same dead button as before.</summary>
    public bool CanRetry => !IsConnected && !IsRetrying;

    /// <summary>
    /// What clicking the row does: switch to it when it answered, ask it again when it did not
    /// (KON-328). Null only for the backend already open.
    /// </summary>
    public ICommand? SwitchCommand { get; init; }

    /// <summary>
    /// What the publisher's own calendar says about the version this backend reports, or null when
    /// there is nothing to say — no published calendar, no readable version, or no answer yet
    /// (KON-370). Arrives after the row is drawn, so the row is rebuilt when it lands.
    /// </summary>
    public VersionSupport? Support { get; init; }

    /// <summary>Whether to show the pill. Only a release its publisher has dropped earns one.</summary>
    public bool IsUnsupported => Support?.IsProblem == true;

    /// <summary>
    /// The sentence behind the row: why the pill is there, or — for a supported release that is behind
    /// on patches — the newer one that exists. Empty when there is nothing to say, which is most of the
    /// time and is the point.
    /// </summary>
    public string SupportSummary => Support switch
    {
        { IsMaintained: false, EolFrom: { } eol } =>
            $"Release {Support.Cycle} has not been supported since {eol.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.",
        { IsMaintained: false } => $"Release {Support.Cycle} is no longer supported.",
        { NewerPatch: { } newer } => $"{newer} is available.",
        _ => string.Empty,
    };
}
