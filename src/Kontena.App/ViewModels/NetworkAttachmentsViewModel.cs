using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>One container attached to the network, as a removable row.</summary>
public sealed record AttachedContainer(string Name);

/// <summary>
/// Managing which containers sit on a network (KON-115).
/// <para>
/// One dialog rather than two flows: "what is on this network" and "put something on it" are the same
/// question asked from either end, and splitting them would mean attaching in one place and detaching in
/// another.
/// </para>
/// </summary>
public partial class NetworkAttachmentsViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onChanged;

    public NetworkAttachmentsViewModel(
        IContainerEngine engine, NetworkSummary network, Action onClose, Func<Task> onChanged)
    {
        _engine = engine;
        _onClose = onClose;
        _onChanged = onChanged;
        NetworkId = network.Id;
        NetworkName = network.Name;
        _ = LoadAsync();
    }

    public string NetworkId { get; }
    public string NetworkName { get; }

    /// <summary>Containers currently on the network.</summary>
    public ObservableCollection<AttachedContainer> Attached { get; } = [];

    /// <summary>Containers that could be added — everything not already on it.</summary>
    public ObservableCollection<string> Candidates { get; } = [];

    [ObservableProperty] private string? _selectedCandidate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public bool CanAttach => !IsBusy && !string.IsNullOrEmpty(SelectedCandidate);

    public bool HasCandidates => Candidates.Count > 0;

    public bool IsEmpty => !IsBusy && Attached.Count == 0;

    partial void OnSelectedCandidateChanged(string? value) => OnPropertyChanged(nameof(CanAttach));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAttach));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Re-reads both lists from the engine after every change. The attached set is the engine's answer,
    /// not something this dialog keeps its own version of — an attach that silently did nothing would
    /// otherwise still look like it worked.
    /// </summary>
    private async Task LoadAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var networks = await _engine.ListNetworksAsync();
            var network = networks.FirstOrDefault(n => n.Id == NetworkId);
            var containers = await _engine.ListContainersAsync(all: true);

            Attached.Clear();
            foreach (var name in (network?.AttachedContainers ?? []).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Attached.Add(new AttachedContainer(name));

            Candidates.Clear();
            foreach (var container in containers
                .Select(c => c.Name)
                .Where(name => !Attached.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                Candidates.Add(container);
            }

            SelectedCandidate = Candidates.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (SelectedCandidate is not { Length: > 0 } name || IsBusy)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            await _engine.ConnectNetworkAsync(name, NetworkId);
            await _onChanged();
        }
        catch (Exception ex)
        {
            // The daemon's own words: "already exists in network", "container is not running" and the
            // rest are clearer than anything this layer would invent.
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task DetachAsync(AttachedContainer? container)
    {
        if (container is null || IsBusy)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            // Forced, because the common case is a running container and the alternative is an error that
            // tells the user to stop something they did not want to stop.
            await _engine.DisconnectNetworkAsync(container.Name, NetworkId, force: true);
            await _onChanged();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private void Close() => _onClose();
}
