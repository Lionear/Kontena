using System.Collections.ObjectModel;
using Avalonia.Threading;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>
/// A shared, bounded log of engine events for the Activity screen. Subscribes to the
/// active engine's <see cref="IContainerEngine.StreamEventsAsync"/>, keeps the most recent
/// entries (newest first), and re-attaches when the active engine changes. Mutations are
/// marshalled to the UI thread so the bound collection stays safe.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    private const int Capacity = 500;
    private CancellationTokenSource? _cts;

    /// <summary>The captured events, newest first.</summary>
    public ObservableCollection<ActivityEntry> Entries { get; } = [];

    /// <summary>Start recording from <paramref name="engine"/>; replaces any prior subscription.</summary>
    /// <param name="resolveName">Best-effort display-name lookup for an event's resource (UI thread).</param>
    public void Attach(IContainerEngine engine, string backend, Func<EngineEvent, string?> resolveName)
    {
        Detach();
        _cts = new CancellationTokenSource();
        _ = WatchAsync(engine, backend, resolveName, _cts.Token);
    }

    public void Detach()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Entries.Clear();
        else
            Dispatcher.UIThread.Post(Entries.Clear);
    }

    private async Task WatchAsync(
        IContainerEngine engine, string backend, Func<EngineEvent, string?> resolveName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in engine.StreamEventsAsync(ct))
                {
                    // Skip noise the engine can't classify — health-check and exec pings
                    // (e.g. Docker's health_status / exec_create|start|die) are not real changes.
                    if (ev.Type == EngineEventType.Unknown)
                        continue;

                    var captured = ev;
                    // Build + insert on the UI thread: resolveName reads UI-owned collections.
                    Dispatcher.UIThread.Post(() =>
                        Add(ActivityEntry.From(captured, backend, resolveName(captured), DateTimeOffset.Now)));
                }

                // Stream ended cleanly — pause, then re-subscribe.
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Engine hiccup (e.g. restart) — back off, then retry.
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Add(ActivityEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > Capacity)
            Entries.RemoveAt(Entries.Count - 1);
    }

    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }
}
