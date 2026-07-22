using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Port forwards page: every tunnel currently open on this cluster, with the local address to use and
/// a way to stop it. The list is the registry's own, so a forward started from a service or a pod appears
/// here straight away — and stays after the modal that started it is closed.
/// </summary>
public sealed partial class PortForwardsViewModel : ViewModelBase, IDisposable
{
    private readonly PortForwardRegistry _registry;

    public PortForwardsViewModel(PortForwardRegistry registry)
    {
        _registry = registry;
        Forwards = registry.Forwards;
        ((INotifyCollectionChanged)Forwards).CollectionChanged += OnForwardsChanged;

        // A tunnel can drop on its own (the pod went away), and IsActive doesn't notify — re-read it when
        // the page is built, which is on every navigation to it.
        RefreshStates();
    }

    public ReadOnlyObservableCollection<ActivePortForward> Forwards { get; }

    public bool IsEmpty => Forwards.Count == 0;

    public bool HasAny => Forwards.Count > 0;

    [RelayCommand]
    private async Task StopAsync(ActivePortForward? entry)
    {
        if (entry is not null)
            await _registry.StopAsync(entry);
    }

    [RelayCommand]
    private async Task StopAllAsync() => await _registry.StopAllAsync();

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
        RefreshStates();
    }

    private void RefreshStates()
    {
        foreach (var forward in Forwards)
            forward.Refresh();
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)Forwards).CollectionChanged -= OnForwardsChanged;
        GC.SuppressFinalize(this);
    }
}
