namespace Kontena.Core.Models;

/// <summary>How Kontena picks its light/dark appearance.</summary>
public enum ThemePreference
{
    /// <summary>Follow the operating system.</summary>
    System = 0,
    Light,
    Dark,
}

/// <summary>
/// User-facing application settings, persisted between launches. Kept engine- and
/// UI-framework-neutral so it can live in Core and be round-tripped in tests.
/// </summary>
public sealed record KontenaSettings
{
    /// <summary>Light/dark/system appearance.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.Dark;

    /// <summary>Tighter rows in the container/image/volume lists.</summary>
    public bool CompactDensity { get; init; }

    /// <summary>Continuously watch for engines starting/stopping.</summary>
    public bool AutoDetectEngines { get; init; } = true;

    /// <summary>Backend id to activate on launch (e.g. "docker"); null = first connected.</summary>
    public string? DefaultEngine { get; init; }

    /// <summary>Start Kontena at login (stored preference; wiring is platform-specific).</summary>
    public bool LaunchAtLogin { get; init; }

    /// <summary>Opt-in anonymous usage stats.</summary>
    public bool SendUsageStats { get; init; }
}
