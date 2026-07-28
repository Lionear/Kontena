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
        IReadOnlyList<PortChoice> ports,
        Action onClose,
        Action? onStarted = null)
    {
        _registry = registry;
        _cluster = cluster;
        _target = target;
        _onClose = onClose;
        _onStarted = onStarted;
        TargetLabel = targetLabel;
        Ports = new ObservableCollection<PortChoice>(ports);

        // No made-up default. This used to fall back to 80, which was right for almost nothing and was
        // presented with the same confidence as a port we actually knew (KON-170). With nothing
        // declared the field starts empty and Start stays disabled until a number is entered.
        _selectedPort = ports.Count > 0 ? ports[0] : null;
        _remotePort = _selectedPort?.Port ?? 0;
        _localPort = LocalPortSuggestion.For(_remotePort, IsLocalPortTaken);
    }

    public string TargetLabel { get; }
    public ObservableCollection<PortChoice> Ports { get; }
    public bool HasPresetPorts => Ports.Count > 0;

    /// <summary>Shown when the target declares no ports, so an empty field reads as a question rather than a fault.</summary>
    public string? NoPortsHint => HasPresetPorts
        ? null
        : "No ports are declared here — enter the one it listens on.";

    [ObservableProperty] private PortChoice? _selectedPort;

    partial void OnSelectedPortChanged(PortChoice? value)
    {
        if (value is { } choice)
            RemotePort = choice.Port;
    }

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

    /// <summary>
    /// Follow the remote port with a local one that stands a chance. Only while the user has not set
    /// the local port themselves — once they have, moving it under them is worse than a poor default.
    /// </summary>
    partial void OnRemotePortChanged(int value)
    {
        if (!_localPortEdited)
            SuggestLocalPort(value);

        OnPropertyChanged(nameof(CanStart));
    }

    private bool _localPortEdited;
    private bool _applyingSuggestion;

    private void SuggestLocalPort(int remote)
    {
        _applyingSuggestion = true;
        try
        {
            LocalPort = LocalPortSuggestion.For(remote, IsLocalPortTaken);
        }
        finally
        {
            _applyingSuggestion = false;
        }
    }

    private bool IsLocalPortTaken(int port) => _registry.OnLocalPort(port) is not null;
    partial void OnLocalPortChanged(int value)
    {
        if (!_applyingSuggestion)
            _localPortEdited = true;

        OnPropertyChanged(nameof(CanStart));
    }
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

/// <summary>
/// One port on offer in the dialog. The label carries where it came from, because a pod's ports come
/// from its containers and "8080" twice over is two identical rows for two different processes.
/// </summary>
public readonly record struct PortChoice(int Port, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Picks the local side of a forward. Pure, so the rules are testable without a registry or a socket.
/// </summary>
internal static class LocalPortSuggestion
{
    /// <summary>Below this, binding needs root on Linux and macOS — so mirroring the remote port fails by design.</summary>
    public const int FirstUnprivileged = 1024;

    /// <summary>How far to walk upward before giving up and letting the start attempt report the clash.</summary>
    private const int MaxProbes = 64;

    /// <summary>
    /// Mirrors the remote port where that can work, because 8080→8080 is what people expect and
    /// remember. For a privileged port it shifts by 8000 instead — the convention that turns 80 into
    /// 8080 and 443 into 8443 — since mirroring there is a suggestion guaranteed to fail unless
    /// Kontena is running as root.
    /// </summary>
    public static int For(int remote, Func<int, bool> taken)
    {
        if (remote <= 0)
            return 0;

        var start = remote < FirstUnprivileged ? remote + 8000 : remote;

        for (var candidate = start; candidate < start + MaxProbes && candidate <= 65535; candidate++)
            if (!taken(candidate))
                return candidate;

        // Everything in reach is spoken for. Hand back the first choice and let the start attempt name
        // the clash — that message says which forward holds the port, which walking further never would.
        return start;
    }
}
