using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>
/// One card in Settings › Extensions — an adapter and whether it is switched on (KON-283).
/// <para>
/// The switch writes through on change rather than behind a Save button, following the rest of this
/// page. Switching one off that something is currently running on asks first, and the question is
/// asked by the page: this row knows what it is, not what else is open.
/// </para>
/// </summary>
public sealed partial class AdapterRow : ViewModelBase
{
    private readonly Action<AdapterRow, bool> _changed;
    private bool _echo;

    public AdapterRow(AdapterEntry adapter, bool enabled, IReadOnlyList<string> inUse,
        Action<AdapterRow, bool> changed)
    {
        Adapter = adapter;
        Chip = new BackendChipInfo(
            adapter.Manifest.Name[..1].ToUpperInvariant(), adapter.Chip?.Glyph, adapter.Chip?.Accent);
        InUse = inUse;
        _isEnabled = enabled;
        _changed = changed;
    }

    public AdapterEntry Adapter { get; }

    /// <summary>What to draw on the card's tile — the adapter's own mark, or its first letter.</summary>
    public BackendChipInfo Chip { get; }

    /// <summary>
    /// The backends this adapter is serving that someone is standing on: the open one, and the one the
    /// next launch would open. Empty means switching it off breaks nothing that is happening now.
    /// </summary>
    public IReadOnlyList<string> InUse { get; }

    public string Id => Adapter.Id;
    public string Name => Adapter.Manifest.Name;
    public string Description => Adapter.Manifest.Description;
    public string Version => "v" + Adapter.Manifest.Version;
    public string AuthorLabel => "by " + (Adapter.Manifest.Author is { Length: > 0 } a ? a : "unknown");

    /// <summary>Which of Kontena's two axes this adapter is on, as the card's kind tag.</summary>
    public string KindLabel => Adapter.Contribution switch
    {
        AdapterContribution.Orchestrator => "Orchestrator",
        AdapterContribution.Tool => "Tool",
        _ => "Container engine",
    };

    /// <summary>
    /// Where it came from. "built-in" and "plugin" are different answers to the same question, and only
    /// one of them corresponds to a directory the user can delete.
    /// </summary>
    public string SourceLabel => Adapter.IsBundled ? "built-in" : "plugin";

    public bool IsBundled => Adapter.IsBundled;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_echo)
            return;

        _changed(this, value);
    }

    /// <summary>
    /// Put the switch back without calling out again — for a confirm the user declined. The switch is
    /// bound two-way, so it has already moved by the time the question is asked.
    /// </summary>
    public void Revert()
    {
        _echo = true;
        IsEnabled = !IsEnabled;
        _echo = false;
    }
}

public partial class SettingsViewModel
{
    private readonly IReadOnlyList<AdapterEntry> _adapters;
    private readonly string? _activeBackend;
    private readonly Func<Task>? _onAdaptersChanged;

    /// <summary>The extensions this installation has, bundled and installed (KON-283).</summary>
    public ObservableCollection<AdapterRow> Adapters { get; } = [];

    /// <summary>
    /// Whether the category is worth offering at all. False only in tests and design-time, which pass
    /// no adapters — a real installation always has the bundled ones.
    /// </summary>
    public bool HasAdapters => Adapters.Count > 0;

    private void RefreshAdapters()
    {
        Adapters.Clear();

        foreach (var adapter in _adapters)
            Adapters.Add(new AdapterRow(adapter, _settings.IsAdapterEnabled(adapter.Id), InUse(adapter), AdapterToggled));

        OnPropertyChanged(nameof(HasAdapters));
    }

    /// <summary>
    /// What this adapter is serving that someone would notice losing: the backend that is open, and the
    /// one the next launch would open. Not everything it contributes — switching off an adapter whose
    /// four kube-contexts nobody has touched should be one click, and a dialog that always appears is
    /// one nobody reads.
    /// </summary>
    private IReadOnlyList<string> InUse(AdapterEntry adapter)
    {
        var wanted = new[] { _activeBackend, _settings.StartupTarget }
            .Where(b => b is { Length: > 0 })
            .Distinct(StringComparer.Ordinal);

        return [.. wanted
            .Where(b => adapter.Owns(b!))
            .Select(b => _backends.FirstOrDefault(e => e.Backend == b)?.Name ?? b!)];
    }

    private void AdapterToggled(AdapterRow row, bool enabled)
    {
        if (enabled || row.InUse.Count == 0)
        {
            _ = ApplyAdapterAsync(row.Id, enabled);
            return;
        }

        // Switching off something that is open takes the user out of it, and the switcher will not have
        // it to go back to. That is not undone by switching it on again — the connection is gone and the
        // page they were on with it — so it is asked before rather than reported after.
        row.Revert();
        RequestConfirm?.Invoke(new ConfirmRequest(
            $"Turn off {row.Name}?",
            $"Kontena will close what it has open on {row.Name} and stop offering its backends. You can turn it back on here at any time.",
            ConfirmLabel: "Turn off",

            // Nothing on disk is destroyed — the adapter stays, and so does everything it manages. What
            // goes is the connection, which is why this is confirmed at all and not styled as a delete.
            Destructive: false,
            Details: [.. row.InUse.Select(b => new ConfirmDetail("IconPlug", "In use now", b))],
            OnConfirm: async () =>
            {
                row.Revert();
                await ApplyAdapterAsync(row.Id, enabled: false);
            }));
    }

    private async Task ApplyAdapterAsync(string id, bool enabled)
    {
        _settings = _store.Update(s => s.WithAdapterEnabled(id, enabled));

        // The provider list is what the switcher is built from, so this is the same rebuild the demo
        // toggle and the remotes use — an adapter switched off has to stop being probed, not just stop
        // being drawn.
        if (_onAdaptersChanged is not null)
            await _onAdaptersChanged();
    }
}
