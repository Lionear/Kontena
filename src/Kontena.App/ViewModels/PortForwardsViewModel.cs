using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Orchestration;
using Kontena.Sdk.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Port forwards page: every tunnel currently open on this cluster, with the local address to use and
/// a way to stop it. The list is the registry's own, so a forward started from a service or a pod appears
/// here straight away — and stays after the modal that started it is closed.
///
/// <para>A tunnel that falls over reports itself (KON-102): the row flips to Dropped while you are looking
/// at it, keeps its local port on the list rather than vanishing, and offers to open it again.</para>
/// </summary>
public sealed partial class PortForwardsViewModel : ViewModelBase, IDisposable
{
    private readonly PortForwardRegistry _registry;

    public PortForwardsViewModel(PortForwardRegistry registry)
    {
        _registry = registry;
        Forwards = registry.Forwards;
        ((INotifyCollectionChanged)Forwards).CollectionChanged += OnForwardsChanged;
        registry.Changed += OnRegistryChanged;
    }

    public ReadOnlyObservableCollection<ActivePortForward> Forwards { get; }

    public bool IsEmpty => Forwards.Count == 0;

    public bool HasAny => Forwards.Count > 0;

    /// <summary>Whether anything can be opened — a dropped tunnel, or one carried over from last time.</summary>
    public bool HasReopenable => _registry.HasReopenable;

    /// <summary>Why a reconnect failed — nearly always the local port taken in the meantime.</summary>
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task StopAsync(ActivePortForward? entry)
    {
        if (entry is not null)
            await _registry.StopAsync(entry);
    }

    [RelayCommand]
    private async Task StopAllAsync() => await _registry.StopAllAsync();

    /// <summary>Open the same tunnel again, on the same local port.</summary>
    [RelayCommand]
    private async Task ReconnectAsync(ActivePortForward? entry)
    {
        if (entry is null)
            return;

        Error = null;
        try
        {
            await _registry.ReconnectAsync(entry);
        }
        catch (Exception ex)
        {
            Error = $"Could not reopen {entry.Address}: {ex.Message}";
        }
    }

    /// <summary>Hand the local port back without losing the row — Resume puts it straight back.</summary>
    [RelayCommand]
    private async Task PauseAsync(ActivePortForward? entry)
    {
        if (entry is not null)
            await _registry.PauseAsync(entry);
    }

    /// <summary>Open everything that isn't running: paused, remembered, and anything that dropped.</summary>
    [RelayCommand]
    private async Task ReopenAllAsync()
    {
        Error = null;
        var failures = new List<string>();

        // A copy: opening a tunnel raises Changed, and the collection is the registry's own.
        foreach (var entry in Forwards.Where(f => !f.IsActive).ToList())
        {
            try
            {
                await _registry.ReconnectAsync(entry);
            }
            catch (Exception ex)
            {
                // One port being taken must not stop the rest — report them together at the end.
                failures.Add($"{entry.Address}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            Error = "Could not open " + string.Join("; ", failures);
    }

    /// <summary>Open the forwarded port in a browser — the common reason for forwarding a web workload.</summary>
    [RelayCommand]
    private static void Open(ActivePortForward? entry)
    {
        if (entry is not null)
            Browser.OpenUrl($"http://{entry.Address}");
    }

    private void OnForwardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAny));
    }

    /// <summary>A row changing state does not change the collection, so the header follows this instead.</summary>
    private void OnRegistryChanged() => OnPropertyChanged(nameof(HasReopenable));

    public void Dispose()
    {
        ((INotifyCollectionChanged)Forwards).CollectionChanged -= OnForwardsChanged;
        _registry.Changed -= OnRegistryChanged;
        GC.SuppressFinalize(this);
    }
}
