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

    /// <summary>Whether to show the warning pill. Only a release its publisher has dropped earns one.</summary>
    public bool IsUnsupported => Support?.IsProblem == true;

    /// <summary>
    /// A newer release on a line that is still maintained (KON-371). Quieter than
    /// <see cref="IsUnsupported"/> and never both: a dropped release is the news, and "there is also a
    /// patch" beside it would be advice about the wrong problem.
    /// </summary>
    public bool HasNewerPatch => Support is { IsProblem: false, NewerPatch: not null };

    /// <summary>The sentence behind the row, worded once in <see cref="VersionSupport.Detail"/>.</summary>
    public string SupportSummary => Support?.Detail ?? string.Empty;
}
