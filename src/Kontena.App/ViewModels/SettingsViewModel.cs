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
    private KontenaSettings _settings;

    public SettingsViewModel(SettingsStore store, KontenaSettings settings, IReadOnlyList<EngineListItem> engines)
    {
        _store = store;
        _settings = settings;
        Engines = engines;

        _theme = settings.Theme;
        _compactDensity = settings.CompactDensity;
        _autoDetect = settings.AutoDetectEngines;
        _launchAtLogin = settings.LaunchAtLogin;
        _terminalFontFamily = settings.TerminalFontFamily;
        _terminalFontSize = settings.TerminalFontSize;
        _terminalLigatures = settings.TerminalLigatures;

        DefaultEngineOptions = [FirstConnectedOption, .. engines.Select(e => e.Name)];
        _selectedDefaultEngine =
            engines.FirstOrDefault(e => e.Backend == settings.DefaultEngine)?.Name ?? FirstConnectedOption;
    }

    private const string FirstConnectedOption = "First connected";

    public IReadOnlyList<EngineListItem> Engines { get; }
    public string[] DefaultEngineOptions { get; }

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ── Category ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _category = "general";

    partial void OnCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsEngines));
        OnPropertyChanged(nameof(IsAbout));
    }

    public bool IsGeneral => Category == "general";
    public bool IsEngines => Category == "engines";
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

    [ObservableProperty] private string _selectedDefaultEngine;
    partial void OnSelectedDefaultEngineChanged(string value) => Save();

    // ── Startup ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _launchAtLogin;
    partial void OnLaunchAtLoginChanged(bool value) => Save();

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
        var backend = Engines.FirstOrDefault(e => e.Name == SelectedDefaultEngine)?.Backend;

        _settings = _settings with
        {
            Theme = Theme,
            CompactDensity = CompactDensity,
            AutoDetectEngines = AutoDetect,
            DefaultEngine = backend,
            LaunchAtLogin = LaunchAtLogin,
            TerminalFontFamily = TerminalFontFamily,
            TerminalFontSize = TerminalFontSize,
            TerminalLigatures = TerminalLigatures,
        };
        _store.Save(_settings);
    }
}
