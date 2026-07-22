using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Owns every live port-forward for as long as the cluster connection lasts.
///
/// <para>The tunnels used to belong to the "Port forward" modal, so closing that window tore them down —
/// which is the opposite of what a forward is for: you open one to keep using it while you work elsewhere
/// in the app. They live here instead, and the modal is reduced to a way of starting one. The Port forwards
/// page lists what is running and is the place to stop it.</para>
///
/// <para>A forward is tied to the cluster it was opened against, so switching backends stops them all —
/// see <c>MainWindowViewModel.ActivateAsync</c>.</para>
/// </summary>
public sealed class PortForwardRegistry
{
    private readonly ObservableCollection<ActivePortForward> _forwards = [];

    public PortForwardRegistry() => Forwards = new ReadOnlyObservableCollection<ActivePortForward>(_forwards);

    /// <summary>The live forwards, in the order they were started.</summary>
    public ReadOnlyObservableCollection<ActivePortForward> Forwards { get; }

    /// <summary>Raised whenever a forward is added or removed, so the sidebar count can follow.</summary>
    public event Action? Changed;

    public int Count => _forwards.Count;

    /// <summary>
    /// Open a tunnel and keep it. <paramref name="localPort"/> 0 (or null) lets the OS pick a free port.
    /// Throws whatever the engine throws — the caller shows it; nothing is registered on failure.
    /// </summary>
    public async Task<ActivePortForward> StartAsync(
        IClusterEngine cluster, ResourceRef target, string targetLabel, int remotePort, int? localPort)
    {
        var forward = await cluster.PortForwardAsync(target, remotePort, localPort);
        var entry = new ActivePortForward(forward, target, targetLabel);
        _forwards.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Tear one tunnel down and drop it from the list. Safe to call twice.</summary>
    public async Task StopAsync(ActivePortForward entry)
    {
        if (!_forwards.Remove(entry))
            return;

        await entry.DisposeAsync();
        Changed?.Invoke();
    }

    /// <summary>Tear every tunnel down — on backend switch and on shutdown.</summary>
    public async Task StopAllAsync()
    {
        if (_forwards.Count == 0)
            return;

        var entries = _forwards.ToList();
        _forwards.Clear();
        foreach (var entry in entries)
            await entry.DisposeAsync();

        Changed?.Invoke();
    }

    /// <summary>The forward already serving <paramref name="localPort"/>, if any — the app's own tunnels
    /// are the one cause of "port in use" it can explain properly.</summary>
    public ActivePortForward? OnLocalPort(int localPort) =>
        _forwards.FirstOrDefault(f => f.LocalPort == localPort);
}

/// <summary>
/// One running tunnel, as the Port forwards page shows it. Implements <see cref="INotifyPropertyChanged"/>
/// by hand rather than pulling an MVVM package into the orchestration layer — exactly one property here
/// changes without the UI asking (<see cref="IsActive"/>, when the tunnel drops on its own).
/// </summary>
public sealed class ActivePortForward : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IPortForward _forward;

    public ActivePortForward(IPortForward forward, ResourceRef target, string targetLabel)
    {
        _forward = forward;
        Target = target;
        TargetLabel = targetLabel;
        StartedAt = DateTimeOffset.Now;
    }

    public ResourceRef Target { get; }

    /// <summary>"name · namespace", as the modal showed it.</summary>
    public string TargetLabel { get; }

    /// <summary>Pod or Service — the two things a forward can point at.</summary>
    public string TargetKind => Target.Kind == GroupVersionKind.Service ? "Service" : "Pod";

    public DateTimeOffset StartedAt { get; }

    public int LocalPort => _forward.LocalPort;
    public int RemotePort => _forward.RemotePort;

    /// <summary>What to type in a browser or client.</summary>
    public string Address => $"localhost:{LocalPort}";

    public string Route => $"localhost:{LocalPort}  →  {RemotePort}";

    /// <summary>False once the tunnel has dropped (the pod went away, the listener closed).</summary>
    public bool IsActive => _forward.IsActive;

    /// <summary>Re-read <see cref="IsActive"/> — the tunnel flips it without telling anyone.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsActive));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ValueTask DisposeAsync() => _forward.DisposeAsync();
}
