namespace Kontena.App.ViewModels;

/// <summary>An engine entry shown in the backend-switcher dropdown.</summary>
public sealed class EngineOption
{
    public required string Name { get; init; }

    /// <summary>Single-letter chip (e.g. "D", "P").</summary>
    public required string Chip { get; init; }

    /// <summary>Secondary line, e.g. version / endpoint.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Whether this is the currently active engine.</summary>
    public bool IsActive { get; init; }
}
