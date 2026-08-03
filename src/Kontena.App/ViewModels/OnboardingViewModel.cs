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

    /// <summary>A roadmap backend that isn't shippable yet (e.g. Apple container).</summary>
    public bool ComingSoon { get; init; }

    /// <summary>Only connected, shippable engines can be selected.</summary>
    public bool Selectable => IsConnected && !ComingSoon;

    public bool ShowRunning => IsConnected && !ComingSoon;
    public bool ShowNotRunning => !IsConnected && !ComingSoon;

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
        bool? showRoadmap = null)
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

        // Roadmap row (not a probe): the native macOS runtime, planned as a later backend. Only where
        // it can ever apply (KON-337) — on Linux and Windows it announced a runtime that platform will
        // never get, at the size of a real engine, on the most expensive screen in the app.
        if (showRoadmap ?? OperatingSystem.IsMacOS())
        {
            items.Add(new OnboardingEngine
            {
                Backend = "apple",
                Name = "Apple container",
                // The mark as path data rather than U+F8FF: the private-use Apple glyph only renders on
                // Apple's own systems, so on Windows and Linux this row showed a tofu box (KON-80).
                Chip = new BackendChipInfo("A", AppleBrand.Glyph, AppleBrand.Accent),
                Detail = "Native macOS runtime · planned backend",
                IsConnected = false,
                ComingSoon = true,
            });
        }

        Engines = items;

        Selected = items.FirstOrDefault(e => e.Selectable);
        if (Selected is not null)
            Selected.IsSelected = true;
    }

    public IReadOnlyList<OnboardingEngine> Engines { get; }

    /// <summary>Persisted on completion; keeps engines in sync as they start/stop.</summary>
    [ObservableProperty] private bool _autoDetect;

    [ObservableProperty] private OnboardingEngine? _selected;

    public bool HasConnectedEngine => Engines.Any(e => e.Selectable);
    public bool NoEngineDetected => !HasConnectedEngine;
    public bool CanContinue => Selected is not null;
    public string ContinueLabel => Selected is not null ? $"Continue with {Selected.Name}" : "Continue";

    partial void OnSelectedChanged(OnboardingEngine? value)
    {
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

    [RelayCommand]
    private void Continue()
    {
        if (Selected is not null)
            _onContinue(Selected.Backend);
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
