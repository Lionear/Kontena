using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>One selectable engine row on the first-run onboarding screen.</summary>
public sealed partial class OnboardingEngine : ObservableObject
{
    public required string Backend { get; init; }
    public required string Name { get; init; }
    public required string Chip { get; init; }
    public required string Detail { get; init; }

    /// <summary>The backend answered a ping and can be picked.</summary>
    public required bool IsConnected { get; init; }

    /// <summary>A roadmap backend that isn't shippable yet (e.g. Apple container).</summary>
    public bool ComingSoon { get; init; }

    /// <summary>Brand accent used for the chip.</summary>
    public required IBrush Accent { get; init; }

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
        Func<IBackendProvider, string>? nameOf = null)
    {
        nameOf ??= p => p.DisplayName;
        _autoDetect = autoDetect;
        _onContinue = onContinue;
        _onSkip = onSkip;
        _onInstallPodman = onInstallPodman;

        var items = new List<OnboardingEngine>();
        foreach (var p in probes)
        {
            if (p.Provider.Backend == fakeBackend)
                continue; // demo backend — not shown at first run

            items.Add(new OnboardingEngine
            {
                Backend = p.Provider.Backend,
                Name = nameOf(p.Provider),
                Chip = p.Provider.Chip,
                Detail = p.Detail ?? string.Empty,
                IsConnected = p.Connected,
                Accent = AccentFor(p.Provider.Backend),
            });
        }

        // Roadmap row (not a probe): the native macOS runtime, planned as a later backend.
        items.Add(new OnboardingEngine
        {
            Backend = "apple",
            Name = "Apple container",
            Chip = "",
            Detail = "Native macOS runtime · planned backend",
            IsConnected = false,
            ComingSoon = true,
            Accent = AccentFor("apple"),
        });

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

    private static SolidColorBrush AccentFor(string backend) => new(Color.Parse(backend switch
    {
        "docker" => "#2496ED",
        "podman" => "#B96FD0",
        "apple" => "#C7C7CC",
        _ => "#22D3AA",
    }));
}
