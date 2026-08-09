using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>One selectable engine row on the first-run onboarding screen.</summary>
public sealed partial class OnboardingEngine : ObservableObject
{
    public required string Backend { get; init; }
    public required string Name { get; init; }
    /// <summary>The engine's mark, or a letter (KON-80).</summary>
    public required BackendChipInfo Chip { get; init; }
    public required string Detail { get; init; }

    /// <summary>The backend answered a ping and can be picked.</summary>
    public required bool IsConnected { get; init; }

    /// <summary>Only a backend that answered can be selected.</summary>
    public bool Selectable => IsConnected;

    public bool ShowRunning => IsConnected;
    public bool ShowNotRunning => !IsConnected;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// One kube-context on the wizard's cluster list (KON-336). Multi-select, unlike the engines: an
/// engine is where you start, clusters are what the switcher carries, and there is no reason to
/// pick only one of them.
/// </summary>
public sealed partial class OnboardingCluster : ObservableObject
{
    public required string Backend { get; init; }
    public required string Name { get; init; }
    public required BackendChipInfo Chip { get; init; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// First-run wizard: detect the container engines already on the machine, let the
/// user pick one (saved as the default), or skip. Reuses the registry probe results;
/// the in-app installer (assisted Podman install) is a later step — for now the
/// "no engine" path links out to the install docs.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    private readonly Action<string?> _onContinue; // chosen backend, or null when skipping
    private readonly Action _onSkip;
    private readonly Action _onInstallPodman;
    private readonly Func<Task> _onRescan;
    private readonly Func<Task> _onStartEngine;

    /// <param name="nameOf">
    /// What to call a backend (KON-119). Defaults to the source's own name; a settings file carried over
    /// from another machine can already hold names, and reading two of them for the same engine at first
    /// run is exactly the confusion the single resolver exists to prevent.
    /// </param>
    public OnboardingViewModel(
        IReadOnlyList<BackendProbe> probes,
        string fakeBackend,
        bool autoDetect,
        Action<string?> onContinue,
        Action onSkip,
        Action onInstallPodman,
        Func<Task> onRescan,
        Func<Task> onStartEngine,
        Func<IBackendProvider, string>? nameOf = null,
        IReadOnlyList<IBackendProvider>? clusters = null,
        Func<string, bool>? clusterTicked = null)
    {
        nameOf ??= p => p.DisplayName;
        _autoDetect = autoDetect;
        _onContinue = onContinue;
        _onSkip = onSkip;
        _onInstallPodman = onInstallPodman;
        _onRescan = onRescan;
        _onStartEngine = onStartEngine;

        var items = new List<OnboardingEngine>();
        foreach (var p in probes)
        {
            if (p.Provider.Backend == fakeBackend)
                continue; // demo backend — not shown at first run

            items.Add(new OnboardingEngine
            {
                Backend = p.Provider.Backend,
                Name = nameOf(p.Provider),
                Chip = BackendChipInfo.For(p.Provider),
                Detail = p.Detail ?? string.Empty,
                IsConnected = p.Connected,
            });
        }

        Engines = items;

        Selected = items.FirstOrDefault(e => e.Selectable);
        if (Selected is not null)
            Selected.IsSelected = true;

        Clusters =
        [
            .. (clusters ?? []).Select(p => new OnboardingCluster
            {
                Backend = p.Backend,
                Name = nameOf(p),
                Chip = BackendChipInfo.For(p),
                IsSelected = clusterTicked?.Invoke(p.Backend) ?? true,
            }),
        ];

        foreach (var cluster in Clusters)
        {
            cluster.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OnboardingCluster.IsSelected))
                    AfterClustersChanged();
            };
        }
    }

    public IReadOnlyList<OnboardingEngine> Engines { get; }

    /// <summary>
    /// The kube-contexts found in the kubeconfig, ticked or not. Read from files, never contacted —
    /// the first run must not wait on an apiserver timeout to draw a list.
    /// <para>
    /// These are not probes: at first run nothing is chosen yet, so the registry was built without a
    /// single cluster provider and there is nothing to probe. Which is exactly the bug — with only a
    /// kubeconfig and no local engine, the wizard said "no engines detected" and offered to install
    /// one (KON-336).
    /// </para>
    /// </summary>
    public IReadOnlyList<OnboardingCluster> Clusters { get; }

    /// <summary>Persisted on completion; keeps engines in sync as they start/stop.</summary>
    [ObservableProperty] private bool _autoDetect;

    [ObservableProperty] private OnboardingEngine? _selected;

    public bool HasConnectedEngine => Engines.Any(e => e.Selectable);
    public bool NoEngineDetected => !HasConnectedEngine;
    public bool HasClusters => Clusters.Count > 0;
    public int SelectedClusterCount => Clusters.Count(c => c.IsSelected);

    /// <summary>A kubeconfig is a way in of its own — a machine with clusters and no engine is set up,
    /// not empty.</summary>
    public bool CanContinue => Selected is not null || SelectedClusterCount > 0;

    public string ContinueLabel => Selected is not null
        ? $"Continue with {Selected.Name}"
        : SelectedClusterCount switch
        {
            0 => "Continue",
            1 => $"Continue with {Clusters.First(c => c.IsSelected).Name}",
            var n => $"Continue with {n} clusters",
        };

    /// <summary>
    /// What this screen is for, in the terms of the machine it is running on. "Connect your container
    /// engine" is the wrong sentence to greet someone who has three clusters and no engine.
    /// </summary>
    public string Headline => HasConnectedEngine || !HasClusters
        ? "Let's connect your container engine"
        : "Let's connect your Kubernetes clusters";

    /// <summary>
    /// Advice to go and install Podman, unless it is the wrong advice: with clusters on offer and no
    /// engine to start, the install guide was the only thing the screen had to say — while three
    /// reachable clusters sat in the kubeconfig (KON-336). It also steps aside for the start assist,
    /// which is about an engine that is already here.
    /// </summary>
    public bool ShowInstallAssist =>
        string.IsNullOrEmpty(FixCommandLine) && !(HasClusters && !HasConnectedEngine);

    partial void OnSelectedChanged(OnboardingEngine? value)
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueLabel));
    }

    partial void OnFixCommandLineChanged(string? value) => OnPropertyChanged(nameof(ShowInstallAssist));

    private void AfterClustersChanged()
    {
        OnPropertyChanged(nameof(SelectedClusterCount));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueLabel));
    }

    [RelayCommand]
    private void Select(OnboardingEngine engine)
    {
        if (!engine.Selectable)
            return;

        foreach (var e in Engines)
            e.IsSelected = false;
        engine.IsSelected = true;
        Selected = engine;
    }

    /// <summary>
    /// Where to land. An engine wins over a cluster when both are picked: entering a cluster swaps the
    /// whole UI mode, and the ticked clusters are in the switcher either way — one click away rather
    /// than one restart away.
    /// </summary>
    [RelayCommand]
    private void Continue()
    {
        var target = Selected?.Backend ?? Clusters.FirstOrDefault(c => c.IsSelected)?.Backend;
        if (target is not null)
            _onContinue(target);
    }

    [RelayCommand]
    private void Skip() => _onSkip();

    [RelayCommand]
    private void InstallPodman() => _onInstallPodman();

    /// <summary>
    /// The command that would start the engine this screen is waiting on, or null when there is no
    /// checked fix. Filled in after the screen is already up: asking systemd takes a moment, and the
    /// reason an engine is not running must never hold up saying that it is not (KON-335).
    /// <para>
    /// Shown rather than only run, the way the engine-down card shows it — this manages a unit on the
    /// user's own machine, so anyone who would rather type it themselves can read it first.
    /// </para>
    /// </summary>
    [ObservableProperty] private string? _fixCommandLine;

    /// <summary>Why starting it failed, or null. Cleared on the next attempt.</summary>
    [ObservableProperty] private string? _fixError;

    /// <summary>
    /// Run the fix on explicit request, never on its own. A successful run rescans, so the row the
    /// user was looking at goes from "Not running" to selectable without anyone restarting anything.
    /// </summary>
    [RelayCommand]
    private Task StartEngine()
    {
        FixError = null;
        return _onStartEngine();
    }

    /// <summary>
    /// Probe again and rebuild this screen. Starting the engine is the obvious thing to do when it
    /// reads "Not running", and until this existed the row stayed grey until the app was restarted —
    /// the one action the screen asks for was the one it could not see you take.
    /// <para>
    /// Async so the generated command disables itself while a probe is in flight; a kubeconfig full
    /// of unreachable contexts takes long enough for a second click to be the natural response.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task Rescan() => _onRescan();
}
