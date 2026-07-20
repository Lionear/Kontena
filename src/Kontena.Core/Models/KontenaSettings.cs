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

    /// <summary>Whether the first-run onboarding (engine connect) has been completed.</summary>
    public bool Onboarded { get; init; }

    /// <summary>Start Kontena at login (stored preference; wiring is platform-specific).</summary>
    public bool LaunchAtLogin { get; init; }

    /// <summary>Primary monospace family for the container terminal.</summary>
    public string TerminalFontFamily { get; init; } = "JetBrains Mono";

    /// <summary>Terminal font size in points.</summary>
    public double TerminalFontSize { get; init; } = 12;

    /// <summary>Enable programming-font ligatures in the terminal.</summary>
    public bool TerminalLigatures { get; init; } = true;

    // ── Window placement (restored on launch) ─────────────────────────────────

    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public bool WindowMaximized { get; init; }
}

/// <summary>Terminal font settings resolved for a session (family carries a mono fallback).</summary>
public sealed record TerminalFont(string Family, double Size, bool Ligatures);
