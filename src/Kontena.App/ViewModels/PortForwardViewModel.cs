using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Port forward" modal: opens a local→remote tunnel to a service or pod via the OAL and shows
/// the live tunnel until it is stopped. Services offer their published ports as presets; pods take a
/// manual remote port.
/// </summary>
public partial class PortForwardViewModel : ViewModelBase, IDisposable
{
    private readonly IClusterEngine _cluster;
    private readonly ResourceRef _target;
    private readonly Action _onClose;
    private IPortForward? _forward;

    public PortForwardViewModel(
        IClusterEngine cluster, ResourceRef target, string targetLabel, IReadOnlyList<int> ports, Action onClose)
    {
        _cluster = cluster;
        _target = target;
        _onClose = onClose;
        TargetLabel = targetLabel;
        Ports = new ObservableCollection<int>(ports);

        _remotePort = ports.Count > 0 ? ports[0] : 80;
        _localPort = _remotePort;
    }

    public string TargetLabel { get; }
    public ObservableCollection<int> Ports { get; }
    public bool HasPresetPorts => Ports.Count > 0;

    [ObservableProperty] private int _remotePort;
    [ObservableProperty] private int _localPort;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _forwardText = string.Empty;

    public bool CanStart => !IsActive && !IsBusy && RemotePort > 0 && LocalPort > 0;

    partial void OnRemotePortChanged(int value) => OnPropertyChanged(nameof(CanStart));
    partial void OnLocalPortChanged(int value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            _forward = await _cluster.PortForwardAsync(_target, RemotePort, LocalPort);
            IsActive = _forward.IsActive;
            ForwardText = $"localhost:{_forward.LocalPort}  →  {_forward.RemotePort}";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_forward is not null)
        {
            await _forward.DisposeAsync();
            _forward = null;
        }
        IsActive = false;
        ForwardText = string.Empty;
    }

    [RelayCommand]
    private void Close()
    {
        _ = StopAsync();
        _onClose();
    }

    public void Dispose()
    {
        if (_forward is not null)
        {
            _ = _forward.DisposeAsync().AsTask();
            _forward = null;
        }
        GC.SuppressFinalize(this);
    }
}
