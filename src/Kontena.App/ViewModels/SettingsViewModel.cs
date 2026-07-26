using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Docker;
using Kontena.App.Services;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>A configured remote engine, as shown in Settings › Engines.</summary>
/// <param name="Remote">The stored configuration.</param>
/// <param name="Connected">Whether it answered the last time backends were probed.</param>
public sealed record RemoteEngineRow(RemoteEngine Remote, bool Connected)
{
    public string Name => Remote.Name;
    public string Endpoint => Remote.Endpoint;

    public string TransportLabel => Remote.Transport == RemoteEngineTransport.Ssh ? "SSH" : "TCP";

    /// <summary>Insecure TCP is stated in the list, not just at the moment of adding it.</summary>
    public bool IsInsecure =>
        Remote.Transport == RemoteEngineTransport.Tcp
        && string.IsNullOrWhiteSpace(Remote.CertificateDirectory);

    public string Status => Connected ? "connected" : "not reachable";
}

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
    /// <param name="autostart">Login-item registration; defaults to this platform's mechanism.</param>
    public SettingsViewModel(
        SettingsStore store, KontenaSettings settings, IReadOnlyList<EngineListItem> engines,
        IReadOnlyList<EngineListItem>? backends = null,
        Func<bool, Task>? onDemoBackendsChanged = null,
        UpdateViewModel? update = null,
        IAutostart? autostart = null,
        ISecretStore? secrets = null,
        Func<Task>? onRemotesChanged = null)
    {
        _autostart = autostart ?? Autostart.Create();
        _secrets = secrets ?? SecretStore.Create();
        _onRemotesChanged = onRemotesChanged;
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

        // Read from the system, not from the file. Someone can delete the autostart entry by hand or
        // switch it off in their desktop's own settings, and then our record is stale — showing it
        // would be claiming an arrangement that no longer exists.
        _launchAtLogin = _autostart.IsSupported ? _autostart.IsEnabled() : settings.LaunchAtLogin;
        RefreshRemotes();
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
    public bool IsPreviewChannel => UpdateChannel == UpdateChannel.Preview;
    public bool IsNightlyChannel => UpdateChannel == UpdateChannel.Nightly;

    partial void OnUpdateChannelChanged(UpdateChannel value)
    {
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPreviewChannel));
        OnPropertyChanged(nameof(IsNightlyChannel));
        OnPropertyChanged(nameof(ChannelHint));
        Save();

        // The channel decides which feed is read, so what was found on the old one no longer
        // applies — ask again rather than leave a stale offer on screen.
        _ = Update?.CheckAsync();
    }

    /// <summary>What the chosen channel means, in terms of how finished the builds on it are.</summary>
    public string ChannelHint => UpdateChannel switch
    {
        UpdateChannel.Nightly =>
            "Cut from develop every night: everything that is finished, and whatever came with it. "
            + "The first place a regression shows up.",
        UpdateChannel.Preview =>
            "Built from main — what has been promoted for the next release, before it is tagged. "
            + "Ahead of stable, past the roughest edges of nightly.",
        _ => "Tagged releases only. This is the one to be on unless you are testing Kontena itself.",
    };

    [RelayCommand]
    private void SetUpdateChannel(string channel) => UpdateChannel = channel switch
    {
        "nightly" => UpdateChannel.Nightly,
        "preview" => UpdateChannel.Preview,
        _ => UpdateChannel.Stable,
    };

    [ObservableProperty] private bool _autoDownloadUpdates;
    partial void OnAutoDownloadUpdatesChanged(bool value) => Save();

    /// <summary>Check now — the manual counterpart of the check on launch.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (Update is not null)
            await Update.CheckAsync(userAsked: true);
    }

    // ── Credentials (KON-52) ────────────────────────────────────────────────

    private readonly ISecretStore _secrets;

    /// <summary>
    /// Whether the OS keychain can be reached. Worth stating before anyone types a password: the answer
    /// decides whether Kontena is able to keep one at all, and it is not something a user can otherwise
    /// find out except by trying.
    /// </summary>
    public bool HasKeychain => _secrets.IsAvailable;

    public string KeychainStatus => _secrets.IsAvailable
        ? "Credentials are stored in your system keychain, never in Kontena's own files. You can inspect and revoke them there."
        : "No system keychain is reachable on this session, so Kontena cannot store credentials. It will not write them anywhere else instead.";

    // ── Remote engines (KON-46) ─────────────────────────────────────────────

    private readonly Func<Task>? _onRemotesChanged;

    public ObservableCollection<RemoteEngineRow> RemoteEngines { get; } = [];

    [ObservableProperty] private string _remoteName = string.Empty;
    [ObservableProperty] private string _remoteHost = string.Empty;
    [ObservableProperty] private string _remoteUser = string.Empty;
    [ObservableProperty] private string _remotePort = string.Empty;
    [ObservableProperty] private string _remoteSocketPath = string.Empty;
    [ObservableProperty] private string _remoteCertificateDirectory = string.Empty;
    [ObservableProperty] private bool _remoteAllowInsecure;
    [ObservableProperty] private bool _remoteIsSsh = true;
    [ObservableProperty] private bool _isRemoteBusy;
    [ObservableProperty] private string? _remoteError;
    [ObservableProperty] private string? _remoteNotice;

    public bool RemoteIsTcp => !RemoteIsSsh;

    /// <summary>Shown for TCP only, and only until certificates are given.</summary>
    public bool ShowInsecureWarning => RemoteIsTcp && string.IsNullOrWhiteSpace(RemoteCertificateDirectory);

    public bool CanAddRemote => !IsRemoteBusy && Draft().Problem is null;

    [RelayCommand]
    private void SetRemoteTransport(string transport) => RemoteIsSsh = transport != "tcp";

    partial void OnRemoteIsSshChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteIsTcp));
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnRemoteFieldChanged();
    }

    partial void OnRemoteNameChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteHostChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemotePortChanged(string value) => OnRemoteFieldChanged();
    partial void OnRemoteAllowInsecureChanged(bool value) => OnRemoteFieldChanged();
    partial void OnIsRemoteBusyChanged(bool value) => OnPropertyChanged(nameof(CanAddRemote));

    partial void OnRemoteCertificateDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnRemoteFieldChanged();
    }

    private void OnRemoteFieldChanged()
    {
        OnPropertyChanged(nameof(CanAddRemote));
        RemoteError = null;
        RemoteNotice = null;
    }

    /// <summary>
    /// The connection the form currently describes. Built rather than validated field by field, so the one
    /// rule that matters — TCP without certificates is refused — lives in the model and not in the view.
    /// </summary>
    private RemoteEngine Draft(string? id = null)
    {
        var port = int.TryParse(RemotePort.Trim(), out var parsed) && parsed > 0 ? parsed : (int?)null;
        var host = RemoteHost.Trim();
        var user = RemoteUser.Trim();
        var socket = RemoteSocketPath.Trim();
        var certificates = RemoteCertificateDirectory.Trim();

        return new RemoteEngine(
            id ?? Guid.NewGuid().ToString("N")[..12],
            string.IsNullOrWhiteSpace(RemoteName) ? host : RemoteName.Trim(),
            RemoteIsSsh ? RemoteEngineTransport.Ssh : RemoteEngineTransport.Tcp,
            host,
            port,
            RemoteIsSsh && user.Length > 0 ? user : null,
            RemoteIsSsh && socket.Length > 0 ? socket : null,
            !RemoteIsSsh && certificates.Length > 0 ? certificates : null,
            !RemoteIsSsh && RemoteAllowInsecure);
    }

    private void RefreshRemotes()
    {
        RemoteEngines.Clear();
        foreach (var remote in _settings.RemoteEngines)
        {
            var connected = _backends.Any(b => b.Backend == remote.Backend && b.Connected);
            RemoteEngines.Add(new RemoteEngineRow(remote, connected));
        }
    }

    /// <summary>
    /// Actually connects, before anything is saved. For SSH that means opening the tunnel and asking the
    /// daemon through it — the only way to tell "the host is reachable" from "the engine answers", which are
    /// different problems with different fixes.
    /// </summary>
    [RelayCommand]
    private async Task TestRemoteAsync()
    {
        var draft = Draft();
        if (draft.Problem is { } problem)
        {
            RemoteError = problem;
            return;
        }

        RemoteError = null;
        RemoteNotice = null;
        IsRemoteBusy = true;
        try
        {
            var info = await Task.Run(async () =>
            {
                var backend = new RemoteDockerEngineProvider(draft).CreateBackend();
                try
                {
                    await backend.PingAsync();
                    return await backend.GetInfoAsync();
                }
                finally
                {
                    // Disposing takes the tunnel with it: a test must not leave a connection behind.
                    (backend as IDisposable)?.Dispose();
                }
            });

            RemoteNotice = $"Connected — {info.DisplayName} {info.Version}.".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // ssh's and the daemon's own words. "Permission denied (publickey)" and "Host key verification
            // failed" say exactly what to fix, and nothing written here would say it better.
            RemoteError = ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddRemoteAsync()
    {
        var draft = Draft();
        if (draft.Problem is { } problem)
        {
            RemoteError = problem;
            return;
        }

        _settings = _settings with { RemoteEngines = [.. _settings.RemoteEngines, draft] };
        _store.Save(_settings);

        RemoteName = string.Empty;
        RemoteHost = string.Empty;
        RemoteUser = string.Empty;
        RemotePort = string.Empty;
        RemoteSocketPath = string.Empty;
        RemoteCertificateDirectory = string.Empty;
        RemoteAllowInsecure = false;
        RemoteNotice = $"Added {draft.Name}.";

        RefreshRemotes();

        // The switcher is built from the provider list, so it has to be rebuilt for the new entry to appear.
        if (_onRemotesChanged is not null)
            await _onRemotesChanged();
    }

    [RelayCommand]
    private async Task RemoveRemoteAsync(RemoteEngineRow? row)
    {
        if (row is null)
            return;

        _settings = _settings with
        {
            RemoteEngines = [.. _settings.RemoteEngines.Where(r => r.Id != row.Remote.Id)],
        };
        _store.Save(_settings);

        // Anything kept in the keychain for this remote goes with it, so a re-add cannot inherit an old
        // secret belonging to a host that is no longer configured.
        await _secrets.DeleteAsync(SecretKeys.Engine(row.Remote.Id));

        RemoteNotice = $"Removed {row.Name}.";
        RefreshRemotes();

        if (_onRemotesChanged is not null)
            await _onRemotesChanged();
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
