namespace Kontena.Core.Models;

/// <summary>How Kontena picks its light/dark appearance.</summary>
public enum ThemePreference
{
    /// <summary>Follow the operating system.</summary>
    System = 0,
    Light,
    Dark,
}

/// <summary>What Kontena connects to when it starts.</summary>
public enum StartupBackend
{
    /// <summary>Whatever was open last — the default, and what most people mean.</summary>
    LastUsed = 0,

    /// <summary>One named backend, every time, whatever was open last.</summary>
    Pinned,

    /// <summary>The first container engine that answers.</summary>
    FirstConnected,
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

    /// <summary>
    /// Legacy: the engine to activate on launch, from before clusters existed and before "last
    /// used" was an option. Superseded by <see cref="Startup"/> and <see cref="PinnedBackend"/>,
    /// and only read now to carry an existing choice forward — see <see cref="ResolvedStartup"/>.
    /// </summary>
    public string? DefaultEngine { get; init; }

    /// <summary>
    /// How the launch backend is chosen. Nullable on purpose: null means the file predates this
    /// setting, and <see cref="ResolvedStartup"/> reads the old <see cref="DefaultEngine"/> instead
    /// — a stored preference should survive an upgrade, not be silently replaced by a default.
    /// </summary>
    public StartupBackend? Startup { get; init; }

    /// <summary>The backend <see cref="StartupBackend.Pinned"/> refers to; a full backend id.</summary>
    public string? PinnedBackend { get; init; }

    /// <summary>
    /// The last backend that connected — a full id, so <c>kubernetes:kind-kind</c> as readily as
    /// <c>docker</c>. Written on every successful activation; it records behaviour, where
    /// <see cref="PinnedBackend"/> records a choice.
    /// </summary>
    public string? LastBackend { get; init; }

    /// <summary>The effective startup mode, honouring a pre-upgrade <see cref="DefaultEngine"/>.</summary>
    public StartupBackend ResolvedStartup => Startup
        ?? (string.IsNullOrEmpty(DefaultEngine) ? StartupBackend.LastUsed : StartupBackend.Pinned);

    /// <summary>The effective pinned backend, honouring a pre-upgrade <see cref="DefaultEngine"/>.</summary>
    public string? ResolvedPinnedBackend =>
        string.IsNullOrEmpty(PinnedBackend) ? DefaultEngine : PinnedBackend;

    /// <summary>
    /// The backend to try first on launch, or null when nothing is remembered or pinned and the
    /// first engine that answers should win.
    /// </summary>
    public string? StartupTarget => ResolvedStartup switch
    {
        StartupBackend.Pinned => NullIfEmpty(ResolvedPinnedBackend),
        StartupBackend.LastUsed => NullIfEmpty(LastBackend),
        _ => null,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Whether the in-memory demo backends (fake engine and fake clusters) appear in the switcher.
    /// <para>
    /// Nullable on purpose: null means "use the build default" — on for a debug build, off for a
    /// release build. A settings file written during development therefore never switches demo
    /// backends on in a release build, which an ordinary bool default could not express.
    /// </para>
    /// </summary>
    public bool? ShowDemoBackends { get; init; }

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

    /// <summary>Recently used build-context folders, most-recent first (for the Build modal).</summary>
    public IReadOnlyList<string> RecentBuildContexts { get; init; } = [];

    // ── Window placement (restored on launch) ─────────────────────────────────

    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public bool WindowMaximized { get; init; }
}

/// <summary>Terminal font settings resolved for a session (family carries a mono fallback).</summary>
public sealed record TerminalFont(string Family, double Size, bool Ligatures);
