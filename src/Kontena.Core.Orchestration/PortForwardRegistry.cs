using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kontena.Core.Models;
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
/// see <c>MainWindowViewModel.ActivateAsync</c>. What survives is the <i>intent</i>: the list is restored
/// on the next visit as entries that are remembered but not open, waiting for a click (KON-105).</para>
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

    /// <summary>How many forwards are held, dropped and remembered ones included.</summary>
    public int Count => _forwards.Count;

    /// <summary>
    /// How many are actually carrying traffic. This is what the sidebar badge counts: a badge that keeps
    /// counting dead tunnels says the wrong thing precisely when it matters.
    /// </summary>
    public int ActiveCount => _forwards.Count(f => f.IsActive);

    /// <summary>Whether anything is waiting to be opened — a dropped tunnel or a remembered one.</summary>
    public bool HasReopenable => _forwards.Any(f => !f.IsActive);

    /// <summary>
    /// How many tunnels fell over on their own. Kept apart from the rest of the not-running rows on
    /// purpose: paused and remembered are states the user put them in, while dropped is something that
    /// happened *to* them — only the last one is worth drawing attention to (KON-107).
    /// </summary>
    public int DroppedCount => _forwards.Count(f => f.State == PortForwardState.Dropped);

    /// <summary>
    /// Open a tunnel and keep it. <paramref name="localPort"/> 0 (or null) lets the OS pick a free port.
    /// Throws whatever the engine throws — the caller shows it; nothing is registered on failure.
    /// </summary>
    public async Task<ActivePortForward> StartAsync(
        IClusterEngine cluster, ResourceRef target, string targetLabel, int remotePort, int? localPort)
    {
        var forward = await cluster.PortForwardAsync(target, remotePort, localPort);
        var entry = new ActivePortForward(forward, cluster, target, targetLabel);
        Add(entry);
        return entry;
    }

    /// <summary>
    /// Put back what was remembered from a previous session, as entries that are <i>not</i> open. A
    /// tunnel that reopens itself on launch is a surprise — into production it is a bad one — and the
    /// local port may since have been taken by something else. Anything already on the list wins, so
    /// restoring twice cannot duplicate a live tunnel.
    /// </summary>
    public void Restore(IClusterEngine cluster, IEnumerable<RememberedPortForward> remembered)
    {
        var added = false;
        foreach (var item in remembered)
        {
            if (_forwards.Any(f => f.LocalPort == item.LocalPort))
                continue;

            var target = new ResourceRef(
                new GroupVersionKind(item.Group, item.Version, item.Kind), item.Namespace, item.Name);
            var entry = new ActivePortForward(cluster, target, item.Label, item.RemotePort, item.LocalPort);
            entry.StateChanged += OnEntryStateChanged;
            _forwards.Add(entry);
            added = true;
        }

        if (added)
            Changed?.Invoke();
    }

    /// <summary>
    /// What to remember for the next session: everything still on the list, open or not. A forward you
    /// explicitly stopped is off the list by then, which is how you say you are done with it.
    /// </summary>
    public IReadOnlyList<RememberedPortForward> Snapshot() => [.. _forwards.Select(f => f.Remember())];

    /// <summary>
    /// Open the tunnel, on the same local port — you wanted that address, and things point at it.
    /// Throws if the port has since been taken; the entry stays closed in that case.
    /// </summary>
    public async Task ReconnectAsync(ActivePortForward entry)
    {
        if (!_forwards.Contains(entry) || entry.IsActive)
            return;

        await entry.ReconnectAsync();
        Changed?.Invoke();
    }

    /// <summary>
    /// Close the tunnel but keep the row, so it can be resumed on the same local port. Use this when
    /// something else needs that port; <see cref="StopAsync"/> is for when you are done with it.
    /// </summary>
    public async Task PauseAsync(ActivePortForward entry)
    {
        if (!_forwards.Contains(entry) || !entry.IsActive)
            return;

        await entry.PauseAsync();
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

    private void Add(ActivePortForward entry)
    {
        // Starting a forward the list already remembers replaces that entry rather than sitting beside
        // it as a second row for the same local port.
        if (_forwards.FirstOrDefault(f => f.LocalPort == entry.LocalPort && !f.IsActive) is { } stale)
        {
            stale.StateChanged -= OnEntryStateChanged;
            _forwards.Remove(stale);
        }

        entry.StateChanged += OnEntryStateChanged;
        _forwards.Add(entry);
        Changed?.Invoke();
    }

    private void OnEntryStateChanged() => Changed?.Invoke();
}

/// <summary>Where one entry on the Port forwards page stands.</summary>
public enum PortForwardState
{
    /// <summary>Carrying traffic.</summary>
    Active,

    /// <summary>Was open and ended on its own — the pod went away, the cluster refused a connection.</summary>
    Dropped,

    /// <summary>Carried over from a previous session and not opened yet.</summary>
    Remembered,

    /// <summary>Closed on purpose, and kept so it can be resumed on the same local port.</summary>
    Paused,
}

/// <summary>
/// One tunnel, as the Port forwards page shows it — open, dropped, or merely remembered. Implements
/// <see cref="INotifyPropertyChanged"/> by hand rather than pulling an MVVM package into the
/// orchestration layer.
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

    // Null while nothing is open: a remembered entry has no tunnel yet, and a stopped one has none
    // any more. The ports live here rather than on the handle so they survive both.
    private IPortForward? _forward;
    private int _localPort;
    private string? _dropReason;

    public ActivePortForward(IPortForward forward, IClusterEngine cluster, ResourceRef target, string targetLabel)
        : this(cluster, target, targetLabel, forward.RemotePort, forward.LocalPort)
    {
        _forward = forward;
        _forward.Closed += OnClosed;
        State = PortForwardState.Active;
    }

    /// <summary>An entry restored from a previous session: known, but not open (KON-105).</summary>
    public ActivePortForward(
        IClusterEngine cluster, ResourceRef target, string targetLabel, int remotePort, int localPort)
    {
        _cluster = cluster;
        _sync = SynchronizationContext.Current;
        _localPort = localPort;
        Target = target;
        TargetLabel = targetLabel;
        RemotePort = remotePort;
        StartedAt = DateTimeOffset.Now;
        State = PortForwardState.Remembered;
    }

    public ResourceRef Target { get; }

    /// <summary>"name · namespace", as the modal showed it.</summary>
    public string TargetLabel { get; }

    /// <summary>Pod or Service — the two things a forward can point at.</summary>
    public string TargetKind => Target.Kind == GroupVersionKind.Service ? "Service" : "Pod";

    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// The local port. Fixed once known — including a port the OS picked — because it is the address
    /// people copied and pointed things at, and reopening on a different one would silently break them.
    /// </summary>
    public int LocalPort => _localPort;

    public int RemotePort { get; }

    /// <summary>What to type in a browser or client.</summary>
    public string Address => $"localhost:{LocalPort}";

    public string Route => $"localhost:{LocalPort}  →  {RemotePort}";

    /// <summary>Open, dropped, or waiting to be opened.</summary>
    public PortForwardState State { get; private set; }

    /// <summary>False once the tunnel has dropped, and before a remembered one is opened.</summary>
    public bool IsActive => _forward?.IsActive == true;

    /// <summary>Why it dropped, in the adapter's words; null while it is up or merely remembered.</summary>
    public string? DropReason => _dropReason;

    /// <summary>Waiting to be opened for the first time this session.</summary>
    public bool IsRemembered => State == PortForwardState.Remembered;

    /// <summary>Was open and ended on its own.</summary>
    public bool IsDropped => State == PortForwardState.Dropped;

    /// <summary>Closed on purpose and waiting to be resumed.</summary>
    public bool IsPaused => State == PortForwardState.Paused;

    /// <summary>The state as the page words it.</summary>
    public string StateText => State switch
    {
        PortForwardState.Active => "Active",
        PortForwardState.Dropped => "Dropped",
        PortForwardState.Paused => "Paused",
        _ => "Not open",
    };

    /// <summary>The sentence behind the state, for the tooltip.</summary>
    public string StateDetail => State switch
    {
        PortForwardState.Active => "The tunnel is carrying traffic.",
        PortForwardState.Dropped => _dropReason ?? "The tunnel ended.",
        PortForwardState.Paused =>
            $"Paused. Nothing is listening on {Address} until you resume it, and the port is free for something else.",
        _ => "Carried over from your last session on this cluster. Nothing is listening until you open it.",
    };

    /// <summary>
    /// Reopening a tunnel that dropped is a different thing from opening one carried over from last
    /// session — and neither is the row's "Open", which opens a browser.
    /// </summary>
    public string ReopenLabel => State switch
    {
        PortForwardState.Dropped => "Reconnect",
        PortForwardState.Paused => "Resume",
        _ => "Reopen",
    };

    /// <summary>Raised when the tunnel drops or is opened, so the registry can pass it on.</summary>
    public event Action? StateChanged;

    /// <summary>What survives to the next session.</summary>
    public RememberedPortForward Remember() => new(
        Target.Kind.Group, Target.Kind.Version, Target.Kind.Kind,
        Target.Namespace, Target.Name, TargetLabel, RemotePort, LocalPort);

    /// <summary>
    /// Close the tunnel but keep the entry, so it can be resumed on the same local port. The gap
    /// between Stop and leaving it running: the port is handed back — which is the usual reason to
    /// want this, something else needs it — without losing what the forward pointed at.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_forward is not { } forward)
            return;

        // Unsubscribed first: tearing it down ourselves is not the tunnel dying, and must not read
        // as one.
        forward.Closed -= OnClosed;
        _forward = null;
        await forward.DisposeAsync();

        _dropReason = null;
        State = PortForwardState.Paused;
        Notify();
    }

    /// <summary>
    /// Open the tunnel on the same local port — reopening a dropped one, resuming a paused one, or
    /// opening a remembered one for the first time this session. Any old handle is disposed first so
    /// the port is certainly free.
    /// </summary>
    public async Task ReconnectAsync()
    {
        if (IsActive)
            return;

        if (_forward is { } previous)
        {
            previous.Closed -= OnClosed;
            await previous.DisposeAsync();
            _forward = null;
        }

        var replacement = await _cluster.PortForwardAsync(Target, RemotePort, _localPort);
        _forward = replacement;
        _forward.Closed += OnClosed;
        _localPort = replacement.LocalPort;
        _dropReason = null;
        State = PortForwardState.Active;

        // Already on the caller's thread — the command that reopens runs on it.
        Notify();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnClosed(string reason)
    {
        _dropReason = reason;
        State = PortForwardState.Dropped;
        if (_sync is null || _sync == SynchronizationContext.Current)
            Notify();
        else
            _sync.Post(_ => Notify(), null);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsRemembered));
        OnPropertyChanged(nameof(IsDropped));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(ReopenLabel));
        OnPropertyChanged(nameof(DropReason));
        OnPropertyChanged(nameof(LocalPort));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Route));
        StateChanged?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ValueTask DisposeAsync()
    {
        if (_forward is not { } forward)
            return ValueTask.CompletedTask;

        forward.Closed -= OnClosed;
        _forward = null;
        return forward.DisposeAsync();
    }
}
