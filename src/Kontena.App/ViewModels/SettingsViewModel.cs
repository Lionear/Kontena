using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>One registry as shown in Settings › Registries.</summary>
/// <param name="Host">Registry host.</param>
/// <param name="Username">Who you are on it, or empty when the config did not say.</param>
/// <param name="IsInherited">
/// True for a login read from the engine's own config. Shown because "why does this registry work when I
/// never logged in here?" deserves an answer — and because Kontena cannot remove what it did not store.
/// </param>
public sealed record RegistryRow(string Host, string Username, bool IsInherited)
{
    public string SourceLabel => IsInherited ? "from your engine config" : "signed in with Kontena";
}

/// <summary>A configured remote engine, as shown in Settings › Engines.</summary>
/// <param name="Remote">The stored configuration.</param>
/// <param name="Connected">Whether it answered the last time backends were probed.</param>
/// <param name="Retrying">True while this remote is being asked again (KON-328).</param>
public sealed record RemoteEngineRow(RemoteEngine Remote, bool Connected, bool Retrying = false)
{
    public string Name => Remote.Name;
    public string Endpoint => Remote.Endpoint;

    public string TransportLabel => Remote.Transport == RemoteEngineTransport.Ssh ? "SSH" : "TCP";

    /// <summary>Insecure TCP is stated in the list, not just at the moment of adding it.</summary>
    public bool IsInsecure =>
        Remote.Transport == RemoteEngineTransport.Tcp
        && string.IsNullOrWhiteSpace(Remote.CertificateDirectory);

    /// <summary>
    /// Why this remote cannot be used, or null (KON-181). The switcher deliberately skips such a
    /// remote rather than offering an entry that cannot connect, on the grounds that this page
    /// explains it — so this page has to actually explain it.
    /// <para>
    /// Reachable without ever touching a form: a settings file edited by hand, synced from another
    /// machine, or written by an older version. "Not reachable" would send someone looking at their
    /// network for a value that was refused before anything was dialled.
    /// </para>
    /// </summary>
    public string? Problem => Remote.Problem;

    public bool HasProblem => Problem is not null;

    public string Status => Problem is not null
        ? "not used"
        : Retrying ? "connecting…" : Connected ? "connected" : "not reachable";

    /// <summary>
    /// Whether to offer a connect attempt on the saved row (KON-328). Withheld only from a remote whose
    /// details are refused before anything is dialled — there is nothing there to reach.
    /// <para>
    /// Present on a connected remote too, which is the "Test connection" that only ever existed inside
    /// the add/edit form: re-testing a stored engine meant Edit → Test → Save, a trip through a form to
    /// do something that changes nothing.
    /// </para>
    /// </summary>
    public bool CanRetry => Problem is null;

    /// <summary>False while the attempt is out, so a second click cannot start a second tunnel.</summary>
    public bool RetryEnabled => !Retrying;

    /// <summary>Naming what the click is for: proving a working one still works is a test, getting a
    /// failed one back is a retry.</summary>
    public string RetryLabel => Retrying ? "Connecting…" : Connected ? "Test" : "Retry";
}

/// <summary>One engine as shown in the Settings › Engines list.</summary>
/// <param name="SourceName">
/// What the backend calls itself, before any name the user gave it (KON-119). Kept alongside
/// <paramref name="Name"/> so the rename field can show the original as its placeholder.
/// </param>
/// <param name="Retrying">True while this engine is being probed again (KON-328).</param>
/// <param name="IsRemote">
/// Whether this row is one of the remotes the user configured, and therefore has a row of its own
/// further down the page with Edit and Remove on it (KON-264). Detected engines carry no actions
/// themselves — you do not remove Docker from an inventory — but a row whose actions live elsewhere
/// has to say where.
/// </param>
public sealed record EngineListItem(
    string Backend, string Name, BackendChipInfo Chip, string Detail, bool Connected, bool IsDefault,
    string SourceName = "", bool Retrying = false, bool IsRemote = false)
{
    /// <summary>
    /// An unreachable engine gets a way to be asked again (KON-328). This list was entirely read-only,
    /// so an engine that started after Kontena did — Docker Desktop is routinely still coming up — had
    /// nothing to click anywhere, and restarting the app was the only way to be seen.
    /// </summary>
    public bool CanRetry => !Connected;

    /// <summary>False while the probe is out, so a second click cannot start a second one.</summary>
    public bool RetryEnabled => !Retrying;

    /// <summary>The button says what is happening, since a probe can take seconds to come back.</summary>
    public string RetryLabel => Retrying ? "Connecting…" : "Retry";
}

/// <summary>
/// One row of Settings › Engines › Names — a backend and what to call it (KON-119).
/// <para>
/// Persists as it is typed rather than behind a Save button: this is one field with an obvious meaning,
/// and a rename that only takes effect after pressing something else is a rename people lose.
/// </para>
/// </summary>
public partial class BackendNameRow : ViewModelBase
{
    private readonly Action<string, string?> _rename;
    private bool _loading;

    public BackendNameRow(string backend, string sourceName, BackendChipInfo chip, string? chosen,
        Action<string, string?> rename)
    {
        Backend = backend;
        SourceName = sourceName;
        Chip = chip;
        _rename = rename;

        _loading = true;
        _name = chosen ?? string.Empty;
        _loading = false;
    }

    public string Backend { get; }

    /// <summary>What the source calls itself — the placeholder, and what an empty field falls back to.</summary>
    public string SourceName { get; }

    public BackendChipInfo Chip { get; }

    [ObservableProperty] private string _name = string.Empty;

    partial void OnNameChanged(string value)
    {
        if (!_loading)
            _rename(Backend, value);
    }
}

/// <summary>
/// A kubeconfig Kontena reads, as shown in Settings › Engines › Kubeconfigs (KON-122).
/// </summary>
/// <param name="Path">The file. Empty for the default one, which has no path to remove.</param>
/// <param name="Label">What the row shows.</param>
/// <param name="CanRemove">
/// False for the default kubeconfig. It is listed so it is visible that Kontena always reads it, but
/// there is nothing to take away — removing it would mean not reading the file every kubectl user has.
/// </param>
public sealed record KubeconfigSource(string Path, string Label, bool CanRemove);

/// <summary>A cluster found in a kubeconfig, whether or not it is in the switcher (KON-120).</summary>
/// <param name="Backend">Backend id.</param>
/// <param name="Name">Context name.</param>
/// <param name="Source">Which kubeconfig it came from.</param>
public sealed record DiscoveredCluster(string Backend, string Name, string Source);

/// <summary>
/// One cluster in Settings › Engines › Clusters, with whether it belongs in the switcher (KON-120).
/// <para>
/// The wizard adds; this is where you take one away again. It matters most right after the change
/// landed: an existing installation keeps everything it had, and this is the only way to thin it out.
/// </para>
/// </summary>
public partial class ClusterChoiceRow : ViewModelBase
{
    private readonly Action<string, bool> _set;
    private bool _loading;

    public ClusterChoiceRow(DiscoveredCluster cluster, bool shown, Action<string, bool> set)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        Backend = cluster.Backend;
        Name = cluster.Name;
        Source = cluster.Source;
        _set = set;

        _loading = true;
        _isShown = shown;
        _loading = false;
    }

    public string Backend { get; }
    public string Name { get; }
    public string Source { get; }

    [ObservableProperty] private bool _isShown;

    partial void OnIsShownChanged(bool value)
    {
        if (!_loading)
            _set(Backend, value);
    }
}

/// <summary>
/// Everything the Settings page needs beyond the three things every caller has — the store, the
/// settings and the engine list. Services it leans on, and callbacks it fires when a change has to
/// reach the shell.
/// <para>
/// A record rather than optional constructor parameters (KON-305). The constructor had grown to
/// fourteen parameters over four separate features, and every branch that added a dependency touched
/// the same signature lines — the changelog problem in another form: both sides add, the resolution
/// is always "keep both", and the conflict carries no information. Adding one here is one property,
/// and no existing call site changes.
/// </para>
/// </summary>
public sealed record SettingsContext
{
    /// <summary>Everything that can be pinned — engines and clusters both. Defaults to the engine
    /// list, so design-time and tests need not supply it.</summary>
    public IReadOnlyList<EngineListItem>? Backends { get; init; }

    /// <summary>Invoked when the demo toggle flips so the shell can rebuild the backend set.</summary>
    public Func<bool, Task>? OnDemoBackendsChanged { get; init; }

    /// <summary>The updater, for the Updates category. Null in design-time and tests.</summary>
    public UpdateViewModel? Update { get; init; }

    /// <summary>Login-item registration; defaults to this platform's mechanism.</summary>
    public IAutostart? Autostart { get; init; }

    /// <summary>Keychain access; defaults to this platform's mechanism.</summary>
    public ISecretStore? Secrets { get; init; }

    /// <summary>Resolves registry logins for the Registries category.</summary>
    public RegistryCredentials? Registries { get; init; }

    /// <summary>The engine to verify a registry login against, read on demand.</summary>
    public Func<IContainerEngine?>? Engine { get; init; }

    /// <summary>Adding or removing a remote changes the provider list the switcher is built from.</summary>
    public Func<Task>? OnRemotesChanged { get; init; }

    /// <summary>A rename changes no connection, so it must not cost a re-probe.</summary>
    public Action? OnNamesChanged { get; init; }

    /// <summary>
    /// Probe one backend again on request (KON-328). The shell owns the probe cache, so the answer has
    /// to be folded in there — a retry that only lit up this page would leave the switcher still
    /// refusing the engine the user just proved was running.
    /// </summary>
    public Func<string, Task>? RetryBackend { get; init; }

    /// <summary>Every cluster in every kubeconfig, not only the chosen ones (KON-120).</summary>
    public IReadOnlyList<DiscoveredCluster> Clusters { get; init; } = [];

    /// <summary>Invoked when a cluster is shown or hidden.</summary>
    public Func<Task>? OnClustersChanged { get; init; }

    /// <summary>The kubeconfigs Kontena reads (KON-122).</summary>
    public IReadOnlyList<KubeconfigSource> Kubeconfigs { get; init; } = [];
}

/// <summary>
/// The Settings page: General (appearance + startup), Engines (auto-detect, default engine, engine
/// list), Registries, Updates and Local clusters. Every change persists immediately via the
/// <see cref="SettingsStore"/>; theme changes apply live.
/// <para>
/// This file holds the state every category shares — construction, the selected category,
/// appearance, startup and the save. A category with enough of its own goes in a partial beside it:
/// <c>SettingsViewModel.Updates.cs</c>, <c>.Registries.cs</c>, <c>.Clusters.cs</c>,
/// <c>.Backends.cs</c>. Nearly every feature adds something here, and one file meant every one of
/// them conflicted with the others (KON-139).
/// </para>
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly List<EngineListItem> _backends;
    private KontenaSettings _settings;

    /// <param name="engines">Container engines, for the detected-engines list.</param>
    /// <param name="context">The services and callbacks the page leans on — see
    /// <see cref="SettingsContext"/>. Optional as a whole: design-time and most tests want none of it.</param>
    public SettingsViewModel(
        SettingsStore store, KontenaSettings settings, IReadOnlyList<EngineListItem> engines,
        SettingsContext? context = null)
    {
        context ??= new SettingsContext();

        _registries = context.Registries;
        _engineForVerify = context.Engine;
        _autostart = context.Autostart ?? Autostart.Create();
        _secrets = context.Secrets ?? SecretStore.Create();
        _onRemotesChanged = context.OnRemotesChanged;
        _onNamesChanged = context.OnNamesChanged;
        _retryBackend = context.RetryBackend;
        _discoveredClusters = context.Clusters;
        Kubeconfigs = [.. context.Kubeconfigs];
        _onClustersChanged = context.OnClustersChanged;
        _backends = [.. context.Backends ?? engines];
        _store = store;
        _settings = settings;
        Engines = [.. engines];
        _onDemoBackendsChanged = context.OnDemoBackendsChanged;
        Update = context.Update;
        // Resolved, not raw: the dropdown shows what updates will actually follow. Choosing one in the
        // page then stores it, which is exactly when "not chosen" should become a choice (KON-123).
        _buildChannel = context.Update?.BuildChannel ?? UpdateChannel.Stable;
        _updateChannel = settings.ResolvedUpdateChannel(_buildChannel);
        _channelWasChosen = settings.UpdateChannel is not null;
        _autoDownloadUpdates = settings.AutoDownloadUpdates;
        _showDemoBackends = BackendCatalog.ShouldIncludeDemo(settings.ShowDemoBackends);

        _theme = settings.Theme;
        _compactDensity = settings.CompactDensity;
        _autoDetect = settings.AutoDetectEngines;
        _diagnosticLogging = settings.DiagnosticLogging;

        // Read from the system, not from the file. Someone can delete the autostart entry by hand or
        // switch it off in their desktop's own settings, and then our record is stale — showing it
        // would be claiming an arrangement that no longer exists.
        _launchAtLogin = _autostart.IsSupported ? _autostart.IsEnabled() : settings.LaunchAtLogin;
        RefreshRemotes();
        RefreshBackendNames();
        RefreshClusters();
        _terminalFontFamily = settings.TerminalFontFamily;
        _terminalFontSize = settings.TerminalFontSize;
        _terminalLigatures = settings.TerminalLigatures;

        // A stored value the picker does not offer joins the list rather than being snapped to the
        // nearest option: the file can be edited by hand, and showing 45 seconds as 30 would be this
        // page lying about what the Alerts page is actually doing.
        _alertRefreshSeconds = settings.AlertRefreshSeconds;
        _alertRefreshChoices = [.. AlertRefresh.Choices.Append(_alertRefreshSeconds).Distinct().Order()];
        AlertRefreshOptions = [.. _alertRefreshChoices.Select(AlertRefresh.Label)];
        _alertRefreshChoice = AlertRefresh.Label(_alertRefreshSeconds);

        RefreshShortcuts();

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

        // What the pin points at, kept by id. The dropdown lists names, and a name can change under it.
        _pinnedBackend = settings.ResolvedStartup == StartupBackend.Pinned
            ? settings.ResolvedPinnedBackend
            : null;
    }

    private const string LastUsedOption = "Continue where I left off";
    private const string FirstConnectedOption = "First connected engine";

    /// <summary>The detected-engines list. Mutable because a rename has to reach it (KON-119).</summary>
    public ObservableCollection<EngineListItem> Engines { get; }

    /// <summary>What Kontena opens on launch: last used, first connected, or one named backend.</summary>
    public ObservableCollection<string> StartupOptions { get; }

    public string Version { get; } = AppVersion.Current;

    /// <summary>When this build was made, or empty for one the build workflow did not make.</summary>
    public string BuildDate { get; } = AppVersion.BuiltOn;

    public bool HasBuildDate => BuildDate.Length > 0;

    // ── Category ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _category = "general";

    partial void OnCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsEngines));
        OnPropertyChanged(nameof(IsUpdates));
        OnPropertyChanged(nameof(IsRegistries));
        OnPropertyChanged(nameof(IsClusters));

        // Re-check on entry rather than on build: tooling can be installed in a terminal while the
        // page is open, and a stale "not installed" is the kind of wrong that makes people click
        // Install twice.
        if (Category == "clusters" && LocalClusters is { } clusters)
            _ = clusters.LoadAsync();

        // Read fresh on entry: a login can have been added by docker login, or revoked in the keychain,
        // since the page was built.
        if (Category == "registries")
            RefreshRegistries();
    }

    public bool IsRegistries => Category == "registries";
    public bool IsClusters => Category == "clusters";
    public bool IsRemoteClusters => Category == "remote-clusters";
    public bool IsGeneral => Category == "general";
    public bool IsEngines => Category == "engines";
    public bool IsUpdates => Category == "updates";

    /// <summary>
    /// Local clusters (KON-109, KON-76). An init property rather than a thirteenth constructor
    /// parameter — this page owns its own state and needs nothing from settings.
    /// </summary>
    public LocalClustersViewModel? LocalClusters { get; init; }

    /// <summary>
    /// Rolling a cluster out onto your own machines (KON-379). Its own section rather than a tab on
    /// the local page: they share a word and nothing else — one makes containers here, the other
    /// installs on machines somewhere, which is the same split the specs and the contracts already have.
    /// </summary>
    public ProvisioningWizardViewModel? RemoteClusters { get; init; }

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

    // ── Diagnostics (KON-389) ───────────────────────────────────────────────

    [ObservableProperty] private bool _diagnosticLogging;

    /// <summary>Where the log is written, so the answer to "which file do I send you" is on screen.</summary>
    public string DiagnosticLogPath { get; } = DiagLog.DefaultPath;

    /// <summary>
    /// Takes effect at once rather than at the next launch. Switching it on is nearly always the
    /// answer to something happening now, and a diagnostic that starts recording tomorrow would miss
    /// the session it was switched on for.
    /// </summary>
    partial void OnDiagnosticLoggingChanged(bool value)
    {
        if (value)
            DiagLog.Open();
        else
            DiagLog.Close();

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

    /// <summary>The pinned backend by id, so a rename cannot move the pin.</summary>
    private string? _pinnedBackend;

    /// <summary>Set while the dropdown is being rebuilt, so relabelling an option is not read as a choice.</summary>
    private bool _relabelling;

    partial void OnSelectedStartupChanged(string value)
    {
        if (_relabelling)
            return;

        _pinnedBackend = _backends.FirstOrDefault(e => e.Name == value)?.Backend;
        Save();
    }

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

    private readonly IAutostart _autostart;

    /// <summary>
    /// Whether the launch-at-login row is offered at all: only where autostart is implemented and the
    /// install has a path that will still work after an update (KON-103). A control that promises
    /// something it does not do is worse than one that is absent.
    /// </summary>
    public bool CanLaunchAtLogin => _autostart.IsSupported;

    [ObservableProperty] private bool _launchAtLogin;

    /// <summary>
    /// Guards against the write below coming back as a property change and writing again.
    /// </summary>
    private bool _applyingAutostart;

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_applyingAutostart)
            return;

        // What the system says after the attempt, not what was asked. If the write did not take, the
        // switch goes back rather than sitting there claiming something that is not true.
        var actual = _autostart.Apply(value);
        if (actual != value)
        {
            _applyingAutostart = true;
            LaunchAtLogin = actual;
            _applyingAutostart = false;
        }

        Save();
    }

    // ── Alerts (KON-393) ────────────────────────────────────────────────────

    /// <summary>
    /// The seconds behind each option, in the order they are offered. Held beside the labels rather
    /// than parsed back out of them: "Every 5 minutes" is text for a person to read, and reading it
    /// back would make the wording load-bearing.
    /// </summary>
    private readonly List<int> _alertRefreshChoices;

    public ObservableCollection<string> AlertRefreshOptions { get; }

    [ObservableProperty] private string _alertRefreshChoice;

    private int _alertRefreshSeconds;

    partial void OnAlertRefreshChoiceChanged(string value)
    {
        var index = AlertRefreshOptions.IndexOf(value);
        if (index < 0)
            return;

        _alertRefreshSeconds = _alertRefreshChoices[index];
        OnPropertyChanged(nameof(AlertRefreshHint));
        Save();
    }

    /// <summary>What the choice costs, spelled out under the picker the way StartupHint is.</summary>
    public string AlertRefreshHint => _alertRefreshSeconds <= 0
        ? "The Alerts page is read when you open it and when you refresh it, and says how old what you see is."
        : "Only while the Alerts page is open. Kontena never polls a cluster you are not looking at.";

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
        // By id: the dropdown shows names, and a name the user changed must not move the pin.
        var pinned = _pinnedBackend is { Length: > 0 } id && _backends.Any(e => e.Backend == id)
            ? id
            : _backends.FirstOrDefault(e => e.Name == SelectedStartup)?.Backend;

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

        _settings = _store.Update(s => s with
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
            // Null until the channel is actually chosen. Save runs on every settings change, so writing
            // the resolved value here would turn "following this build" into a choice the moment someone
            // flipped the theme (KON-123).
            UpdateChannel = _channelWasChosen ? UpdateChannel : null,
            AutoDownloadUpdates = AutoDownloadUpdates,
            TerminalFontFamily = TerminalFontFamily,
            TerminalFontSize = TerminalFontSize,
            TerminalLigatures = TerminalLigatures,
            AlertRefreshSeconds = _alertRefreshSeconds,
            DiagnosticLogging = DiagnosticLogging,
            Shortcuts = _shortcutOverrides,
        });
    }
}
