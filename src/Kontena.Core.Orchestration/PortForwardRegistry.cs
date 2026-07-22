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

    /// <summary>
    /// Raised whenever a forward is added, removed or changes state, so the sidebar count can follow.
    /// </summary>
    public event Action? Changed;

    /// <summary>How many forwards are held, dropped ones included — a dead forward is kept on the list.</summary>
    public int Count => _forwards.Count;

    /// <summary>
    /// How many are actually carrying traffic. This is what the sidebar badge counts: a badge that keeps
    /// counting dead tunnels says the wrong thing precisely when it matters.
    /// </summary>
    public int ActiveCount => _forwards.Count(f => f.IsActive);

    /// <summary>
    /// Open a tunnel and keep it. <paramref name="localPort"/> 0 (or null) lets the OS pick a free port.
    /// Throws whatever the engine throws — the caller shows it; nothing is registered on failure.
    /// </summary>
    public async Task<ActivePortForward> StartAsync(
        IClusterEngine cluster, ResourceRef target, string targetLabel, int remotePort, int? localPort)
    {
        var forward = await cluster.PortForwardAsync(target, remotePort, localPort);
        var entry = new ActivePortForward(forward, cluster, target, targetLabel);
        entry.StateChanged += OnEntryStateChanged;
        _forwards.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// Open the tunnel again, on the same local port — you wanted that address, and things pointed at it.
    /// Throws if the port has since been taken; the entry stays dropped in that case.
    /// </summary>
    public async Task ReconnectAsync(ActivePortForward entry)
    {
        if (!_forwards.Contains(entry) || entry.IsActive)
            return;

        await entry.ReconnectAsync();
        Changed?.Invoke();
    }

    /// <summary>Tear one tunnel down and drop it from the list. Safe to call twice.</summary>
    public async Task StopAsync(ActivePortForward entry)
    {
        if (!_forwards.Remove(entry))
            return;

        entry.StateChanged -= OnEntryStateChanged;
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
        {
            entry.StateChanged -= OnEntryStateChanged;
            await entry.DisposeAsync();
        }

        Changed?.Invoke();
    }

    /// <summary>The forward already serving <paramref name="localPort"/>, if any — the app's own tunnels
    /// are the one cause of "port in use" it can explain properly.</summary>
    public ActivePortForward? OnLocalPort(int localPort) =>
        _forwards.FirstOrDefault(f => f.LocalPort == localPort);

    private void OnEntryStateChanged() => Changed?.Invoke();
}

/// <summary>
/// One running tunnel, as the Port forwards page shows it. Implements <see cref="INotifyPropertyChanged"/>
/// by hand rather than pulling an MVVM package into the orchestration layer.
///
/// <para>A tunnel can end without anyone asking — the pod went away, the apiserver refused the next
/// connection — and the adapter is the only thing that knows when. It says so through
/// <see cref="IPortForward.Closed"/>, and this turns that into a property change (KON-102). It is
/// raised on whichever thread noticed, so the notification is posted back to the context this entry
/// was created on; the UI binds to it directly.</para>
/// </summary>
public sealed class ActivePortForward : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IClusterEngine _cluster;
    private readonly SynchronizationContext? _sync;
    private IPortForward _forward;
    private string? _dropReason;

    public ActivePortForward(IPortForward forward, IClusterEngine cluster, ResourceRef target, string targetLabel)
    {
        _forward = forward;
        _cluster = cluster;
        _sync = SynchronizationContext.Current;
        Target = target;
        TargetLabel = targetLabel;
        StartedAt = DateTimeOffset.Now;
        _forward.Closed += OnClosed;
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

    /// <summary>Why it dropped, in the adapter's words; null while it is up.</summary>
    public string? DropReason => _dropReason;

    /// <summary>Raised when the tunnel drops or is reconnected, so the registry can pass it on.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Open the same tunnel again on the same local port. Only meaningful once it has dropped; the
    /// old handle is disposed first so the port is certainly free.
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (IsActive)
            return;

        _forward.Closed -= OnClosed;
        await _forward.DisposeAsync();

        var replacement = await _cluster.PortForwardAsync(Target, RemotePort, LocalPort);
        _forward = replacement;
        _forward.Closed += OnClosed;
        _dropReason = null;

        // Already on the caller's thread — the command that reconnects runs on it.
        Notify();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnClosed(string reason)
    {
        _dropReason = reason;
        if (_sync is null || _sync == SynchronizationContext.Current)
            Notify();
        else
            _sync.Post(_ => Notify(), null);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(DropReason));
        StateChanged?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ValueTask DisposeAsync()
    {
        _forward.Closed -= OnClosed;
        return _forward.DisposeAsync();
    }
}
