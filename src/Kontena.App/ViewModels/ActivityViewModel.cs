using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>One line in the Activity timeline, derived from an <see cref="EngineEvent"/>.</summary>
public sealed class ActivityEntry
{
    public required EngineEventType Type { get; init; }
    public required ResourceKind Kind { get; init; }

    /// <summary>Verb for the action, e.g. "Started", "Pulled".</summary>
    public required string ActionText { get; init; }

    /// <summary>Resolved resource name, or a short id fallback.</summary>
    public required string Name { get; init; }

    /// <summary>Lower-case resource kind, e.g. "container".</summary>
    public required string KindLabel { get; init; }

    /// <summary>Icons.axaml resource key for the badge glyph.</summary>
    public required string IconKey { get; init; }

    /// <summary>Theme brush key for the badge accent (Success/Warn/Danger/Info/Accent/…).</summary>
    public required string AccentKey { get; init; }

    public required string Backend { get; init; }

    /// <summary>The owning backend's mark, resolved from its id (KON-80).</summary>
    public required BackendChipInfo Chip { get; init; }

    /// <summary>Local wall-clock time, e.g. "14:32:07".</summary>
    public required string Time { get; init; }

    /// <summary>Relative age computed at capture (does not tick), e.g. "2m ago".</summary>
    public required string Ago { get; init; }

    public static ActivityEntry From(EngineEvent ev, string backend, string? resolvedName, DateTimeOffset now)
    {
        var (action, icon, accent) = Describe(ev.Type);
        return new ActivityEntry
        {
            Type = ev.Type,
            Kind = ev.ResourceKind,
            ActionText = action,
            Name = string.IsNullOrWhiteSpace(resolvedName) ? ShortId(ev.ResourceId) : resolvedName!,
            KindLabel = ev.ResourceKind.ToString().ToLowerInvariant(),
            IconKey = icon,
            AccentKey = accent,
            Backend = backend,
            Chip = BackendChips.For(backend),
            Time = ev.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Ago = RelativeAge(now - ev.Timestamp),
        };
    }

    private static (string action, string icon, string accent) Describe(EngineEventType type) => type switch
    {
        EngineEventType.Created => ("Created", "IconPlus", "Info"),
        EngineEventType.Started => ("Started", "IconPlay", "Success"),
        EngineEventType.Stopped => ("Stopped", "IconStop", "Warn"),
        EngineEventType.Paused => ("Paused", "IconStop", "Warn"),
        EngineEventType.Unpaused => ("Resumed", "IconPlay", "Success"),
        EngineEventType.Died => ("Exited", "IconStop", "Danger"),
        EngineEventType.Removed => ("Removed", "IconTrash", "Danger"),
        EngineEventType.Pulled => ("Pulled", "IconDownload", "Accent"),
        _ => ("Changed", "IconInfo", "TextDim"),
    };

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    private static string RelativeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(5)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}

/// <summary>
/// The Activity page: a live, reverse-chronological timeline of engine events, sourced
/// from the shared <see cref="ActivityLog"/>. Supports command-bar search and a kind filter.
/// </summary>
public sealed partial class ActivityViewModel : ViewModelBase, IListPage
{
    public string SearchPlaceholder => "Search activity…";

    private readonly ActivityLog _log;

    public ActivityViewModel(ActivityLog log)
    {
        _log = log;
        _log.Entries.CollectionChanged += OnLogChanged;
        ApplyFilter();
    }

    /// <summary>The filtered entries shown in the feed (newest first).</summary>
    public ObservableCollection<ActivityEntry> Items { get; } = [];

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();

    // ── Search (shared command bar) ──────────────────────────────────────────

    [ObservableProperty] private string _searchText = string.Empty;
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ── Kind filter chips ────────────────────────────────────────────────────

    /// <summary>null = all kinds.</summary>
    [ObservableProperty] private ResourceKind? _kindFilter;

    public bool IsAll => KindFilter is null;
    public bool IsContainers => KindFilter == ResourceKind.Container;
    public bool IsImages => KindFilter == ResourceKind.Image;
    public bool IsVolumes => KindFilter == ResourceKind.Volume;
    public bool IsNetworks => KindFilter == ResourceKind.Network;

    partial void OnKindFilterChanged(ResourceKind? value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsContainers));
        OnPropertyChanged(nameof(IsImages));
        OnPropertyChanged(nameof(IsVolumes));
        OnPropertyChanged(nameof(IsNetworks));
        ApplyFilter();
    }

    [RelayCommand]
    private void SetFilter(string kind) => KindFilter = kind switch
    {
        "containers" => ResourceKind.Container,
        "images" => ResourceKind.Image,
        "volumes" => ResourceKind.Volume,
        "networks" => ResourceKind.Network,
        _ => null,
    };

    [RelayCommand]
    private void Clear() => _log.Clear();

    public bool IsEmpty => Items.Count == 0;

    // IListPage: data is pushed live, so there is nothing to load.
    public bool HasLoaded => true;
    public Task LoadAsync() => Task.CompletedTask;

    private void ApplyFilter()
    {
        var desired = _log.Entries.Where(Matches).ToList();

        Items.Clear();
        foreach (var entry in desired)
            Items.Add(entry);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Matches(ActivityEntry e)
    {
        if (KindFilter is { } k && e.Kind != k)
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            return e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.ActionText.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.KindLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
