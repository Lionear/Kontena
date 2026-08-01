using System.Windows.Input;

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
}
