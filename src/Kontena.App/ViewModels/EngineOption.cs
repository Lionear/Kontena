using System.Windows.Input;

namespace Kontena.App.ViewModels;

/// <summary>An engine entry shown in the backend-switcher dropdown.</summary>
public sealed class EngineOption
{
    public required string Backend { get; init; }

    public required string Name { get; init; }

    /// <summary>Single-letter chip (e.g. "D", "P").</summary>
    public required string Chip { get; init; }

    /// <summary>Secondary line — version/endpoint, or a short "not connected" reason.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Whether this is the currently active engine.</summary>
    public bool IsActive { get; init; }

    /// <summary>Whether the backend answered a ping.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Switches to this engine; null when it's active or not connected.</summary>
    public ICommand? SwitchCommand { get; init; }
}
