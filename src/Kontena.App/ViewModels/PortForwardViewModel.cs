using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Port forward" modal: opens a local→remote tunnel to a service or pod via the OAL. Services offer
/// their published ports as presets; pods take a manual remote port.
///
/// <para>The modal no longer <i>owns</i> the tunnel — <see cref="PortForwardRegistry"/> does, so closing
/// this window leaves the forward running and you manage it from the Port forwards page. That is the whole
/// point of a forward: you start one to keep using it while you work elsewhere.</para>
/// </summary>
public partial class PortForwardViewModel : ViewModelBase
{
    private readonly PortForwardRegistry _registry;
    private readonly IClusterEngine _cluster;
    private readonly ResourceRef _target;
    private readonly Action _onClose;
    private readonly Action? _onStarted;

    public PortForwardViewModel(
        PortForwardRegistry registry,
        IClusterEngine cluster,
        ResourceRef target,
        string targetLabel,
        IReadOnlyList<int> ports,
        Action onClose,
        Action? onStarted = null)
    {
        _registry = registry;
        _cluster = cluster;
        _target = target;
        _onClose = onClose;
        _onStarted = onStarted;
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

    /// <summary>Once started, the footer's only action is Done — stopping happens on the Port forwards page,
    /// which is also where the forward will still be after this window closes.</summary>
    public string CloseLabel => IsActive ? "Done" : "Cancel";

    partial void OnRemotePortChanged(int value) => OnPropertyChanged(nameof(CanStart));
    partial void OnLocalPortChanged(int value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CloseLabel));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            // A local port this app is already forwarding fails deep inside the listener with a bare
            // "address in use"; say which forward has it instead.
            if (_registry.OnLocalPort(LocalPort) is { } clash)
            {
                Error = $"Local port {LocalPort} is already forwarding to {clash.TargetLabel}.";
                return;
            }

            var entry = await _registry.StartAsync(_cluster, _target, TargetLabel, RemotePort, LocalPort);
            IsActive = true;
            ForwardText = entry.Route;
            _onStarted?.Invoke();
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
    private void Close() => _onClose();
}
