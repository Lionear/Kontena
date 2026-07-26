using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>One engine as shown in the Settings › Engines list.</summary>
public sealed record EngineListItem(
    string Backend, string Name, string Chip, string Detail, bool Connected, bool IsDefault);

/// <summary>
/// The Settings page: General (appearance + startup), Engines (auto-detect,
/// default engine, engine list) and About. Every change persists immediately via
/// the <see cref="SettingsStore"/>; theme changes apply live.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly IReadOnlyList<EngineListItem> _backends;
    private KontenaSettings _settings;

    /// <param name="engines">Container engines, for the detected-engines list.</param>
    /// <param name="backends">Everything that can be pinned — engines and clusters both. Defaults
    /// to <paramref name="engines"/> so design-time and tests need not supply it.</param>
    /// <param name="onDemoBackendsChanged">Invoked when the demo toggle flips so the shell can
    /// rebuild the backend set. Null in design-time and test contexts.</param>
    /// <param name="update">The updater, for the Updates category. Null in design-time and tests.</param>
    public SettingsViewModel(
        SettingsStore store, KontenaSettings settings, IReadOnlyList<EngineListItem> engines,
        IReadOnlyList<EngineListItem>? backends = null,
        Func<bool, Task>? onDemoBackendsChanged = null,
        UpdateViewModel? update = null)
    {
        _backends = backends ?? engines;
        _store = store;
        _settings = settings;
        Engines = engines;
        _onDemoBackendsChanged = onDemoBackendsChanged;
        Update = update;
        _updateChannel = settings.UpdateChannel;
        _autoDownloadUpdates = settings.AutoDownloadUpdates;
        _showDemoBackends = BackendCatalog.ShouldIncludeDemo(settings.ShowDemoBackends);

        _theme = settings.Theme;
        _compactDensity = settings.CompactDensity;
        _autoDetect = settings.AutoDetectEngines;
        _launchAtLogin = settings.LaunchAtLogin;
        _terminalFontFamily = settings.TerminalFontFamily;
        _terminalFontSize = settings.TerminalFontSize;
        _terminalLigatures = settings.TerminalLigatures;

        // One control, not two: "which backend" and "how is it chosen" were separate settings that
        // could contradict each other. The list is the answer to a single question.
        StartupOptions = [LastUsedOption, FirstConnectedOption, .. _backends.Select(e => e.Name)];
        _selectedStartup = settings.ResolvedStartup switch
        {
            StartupBackend.Pinned =>
                _backends.FirstOrDefault(e => e.Backend == settings.ResolvedPinnedBackend)?.Name ?? LastUsedOption,
            StartupBackend.FirstConnected => FirstConnectedOption,
            _ => LastUsedOption,
        };
    }

    private const string LastUsedOption = "Continue where I left off";
    private const string FirstConnectedOption = "First connected engine";

    public IReadOnlyList<EngineListItem> Engines { get; }

    /// <summary>What Kontena opens on launch: last used, first connected, or one named backend.</summary>
    public string[] StartupOptions { get; }

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ── Category ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _category = "general";

    partial void OnCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsEngines));
        OnPropertyChanged(nameof(IsUpdates));
        OnPropertyChanged(nameof(IsAbout));
    }

    public bool IsGeneral => Category == "general";
    public bool IsEngines => Category == "engines";
    public bool IsUpdates => Category == "updates";
    public bool IsAbout => Category == "about";

    [RelayCommand]
    private void SelectCategory(string category) => Category = category;

    // ── Appearance ──────────────────────────────────────────────────────────

    [ObservableProperty] private ThemePreference _theme;

    public bool IsLightTheme => Theme == ThemePreference.Light;
    public bool IsDarkTheme => Theme == ThemePreference.Dark;
    public bool IsSystemTheme => Theme == ThemePreference.System;

    partial void OnThemeChanged(ThemePreference value)
    {
        ThemeApplier.Apply(value);
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        Save();
    }

    [ObservableProperty] private bool _compactDensity;

    partial void OnCompactDensityChanged(bool value)
    {
        DensityApplier.Apply(value);
        Save();
    }

    [RelayCommand]
    private void SetTheme(string theme) => Theme = theme switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => ThemePreference.System,
    };

    // ── Engines ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _autoDetect;
    partial void OnAutoDetectChanged(bool value) => Save();

    [ObservableProperty] private string _selectedStartup;
    partial void OnSelectedStartupChanged(string value) => Save();

    /// <summary>What the current choice means, spelled out under the picker.</summary>
    public string StartupHint => SelectedStartup switch
    {
        LastUsedOption => "Kontena reopens the engine or cluster you were on when you last quit.",
        FirstConnectedOption => "Kontena opens the first container engine that answers, and never a cluster.",
        _ => $"Kontena always opens {SelectedStartup}, whatever you were on last.",
    };

    // ── Demo backends (development only) ────────────────────────────────────

    private readonly Func<bool, Task>? _onDemoBackendsChanged;

    /// <summary>
    /// Whether the row is offered at all. Demo backends are a development aid, so in an ordinary
    /// release build the toggle would be meaningless and is hidden entirely.
    /// </summary>
    public bool CanToggleDemoBackends { get; } = BackendCatalog.DemoAllowed;

    [ObservableProperty] private bool _showDemoBackends;

    partial void OnShowDemoBackendsChanged(bool value)
    {
        Save();

        // Rebuilding the backend set is the shell's job; this only records the choice.
        if (_onDemoBackendsChanged is not null)
            _ = _onDemoBackendsChanged(value);
    }

    // ── Startup ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the launch-at-login row is offered at all. False everywhere today: nothing writes an
    /// autostart entry — no <c>~/.config/autostart</c> file, no Run key, no LaunchAgent — so the
    /// switch only ever recorded its own position. A control that promises something it does not do
    /// is worse than one that is absent, so it stays hidden until KON-103 makes it true.
    /// </summary>
    /// <remarks>Never assigned, so false — the point is that there is nothing to assign it from yet.</remarks>
    public bool CanLaunchAtLogin { get; }

    [ObservableProperty] private bool _launchAtLogin;
    partial void OnLaunchAtLoginChanged(bool value) => Save();

    // ── Updates (KON-110) ───────────────────────────────────────────────────

    /// <summary>The updater, so the category can show its state and trigger a check. Null in tests.</summary>
    public UpdateViewModel? Update { get; }

    /// <summary>Whether the category is offered at all — it is meaningless without an updater.</summary>
    public bool HasUpdates => Update is not null;

    /// <summary>
    /// Whether this install can replace itself. False for a distro package or an unpacked archive:
    /// the channel and auto-download rows would then promise something that cannot happen.
    /// </summary>
    public bool CanSelfUpdate => Update?.CanSelfUpdate ?? false;

    /// <summary>
    /// The two halves of the category, as named properties rather than a binding-side negation:
    /// they must never both be on screen, and one expression that can silently fail to evaluate is
    /// exactly how they end up contradicting each other.
    /// </summary>
    public bool ShowUpdatePreferences => CanSelfUpdate;

    public bool ShowUnsupportedNotice => HasUpdates && !CanSelfUpdate;

    [ObservableProperty] private UpdateChannel _updateChannel;

    public bool IsStableChannel => UpdateChannel == UpdateChannel.Stable;
    public bool IsNightlyChannel => UpdateChannel == UpdateChannel.Nightly;

    partial void OnUpdateChannelChanged(UpdateChannel value)
    {
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsNightlyChannel));
        OnPropertyChanged(nameof(ChannelHint));
        Save();

        // The channel decides which feed is read, so what was found on the old one no longer
        // applies — ask again rather than leave a stale offer on screen.
        _ = Update?.CheckAsync();
    }

    public string ChannelHint => UpdateChannel == UpdateChannel.Nightly
        ? "Nightly builds are cut from develop every night. They carry what is finished but not released — and whatever came with it."
        : "Tagged releases only. This is the one to be on unless you are testing Kontena itself.";

    [RelayCommand]
    private void SetUpdateChannel(string channel) =>
        UpdateChannel = channel == "nightly" ? UpdateChannel.Nightly : UpdateChannel.Stable;

    [ObservableProperty] private bool _autoDownloadUpdates;
    partial void OnAutoDownloadUpdatesChanged(bool value) => Save();

    /// <summary>Check now — the manual counterpart of the check on launch.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (Update is not null)
            await Update.CheckAsync(userAsked: true);
    }

    // ── Terminal ────────────────────────────────────────────────────────────

    public string[] FontFamilies { get; } =
        ["JetBrains Mono", "Cascadia Code", "Fira Code", "Consolas", "Menlo", "monospace"];

    [ObservableProperty] private string _terminalFontFamily;
    partial void OnTerminalFontFamilyChanged(string value) => Save();

    [ObservableProperty] private double _terminalFontSize;
    partial void OnTerminalFontSizeChanged(double value) => Save();

    [ObservableProperty] private bool _terminalLigatures;
    partial void OnTerminalLigaturesChanged(bool value) => Save();

    // ── Persist ─────────────────────────────────────────────────────────────

    private void Save()
    {
        var pinned = _backends.FirstOrDefault(e => e.Name == SelectedStartup)?.Backend;
        var startup = SelectedStartup switch
        {
            FirstConnectedOption => StartupBackend.FirstConnected,
            LastUsedOption => StartupBackend.LastUsed,
            _ when pinned is not null => StartupBackend.Pinned,

            // A pinned backend that is no longer in the list (kube-context removed while Settings
            // was open) must not silently become a pin on nothing.
            _ => StartupBackend.LastUsed,
        };

        OnPropertyChanged(nameof(StartupHint));

        _settings = _settings with
        {
            Theme = Theme,
            CompactDensity = CompactDensity,
            AutoDetectEngines = AutoDetect,
            Startup = startup,
            PinnedBackend = startup == StartupBackend.Pinned ? pinned : null,

            // The legacy field is cleared once a choice is made here, so the migration in
            // ResolvedStartup cannot come back and override what the user just picked.
            DefaultEngine = null,
            ShowDemoBackends = ShowDemoBackends,
            LaunchAtLogin = LaunchAtLogin,
            UpdateChannel = UpdateChannel,
            AutoDownloadUpdates = AutoDownloadUpdates,
            TerminalFontFamily = TerminalFontFamily,
            TerminalFontSize = TerminalFontSize,
            TerminalLigatures = TerminalLigatures,
        };
        _store.Save(_settings);
    }
}
