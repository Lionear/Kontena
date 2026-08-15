namespace Kontena.Sdk.Orchestration.Models;

/// <summary>Severity of a cluster event.</summary>
public enum EventSeverity
{
    Normal,
    Warning,
}

/// <summary>
/// A cluster event (the neutral form of a core <c>v1 Event</c>) — what shows in the events
/// feed and the pod/workload detail drawers. Named <c>ClusterEvent</c> to avoid colliding
/// with <see cref="System.EventHandler"/>-style "Event".
/// </summary>
public sealed record ClusterEvent
{
    public required string Reason { get; init; }
    public required string Message { get; init; }

    public EventSeverity Severity { get; init; } = EventSeverity.Normal;

    /// <summary>The object the event is about.</summary>
    public ResourceRef InvolvedObject { get; init; }

    /// <summary>Reporting component, e.g. "kubelet", "deployment-controller".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>How many times this event has fired.</summary>
    public int Count { get; init; } = 1;

    /// <summary>When it was last seen (UTC).</summary>
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>The kind of change a watch reports.</summary>
public enum WatchEventType
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// One item from a <c>WatchAsync</c> informer stream: what happened, and to which resource. Generic
/// over kind via <see cref="ResourceRef"/> + <see cref="GroupVersionKind"/>, so a single stream type
/// serves every resource — including CRDs.
/// <para>
/// It used to carry the resource's manifest as YAML too, and nothing ever read it (KON-355). The
/// Kubernetes adapter built one per event, at 0.29 ms and 150 KB of garbage each, for every kind
/// every live page follows — and the pages that follow them only ever ask <i>whether</i> something
/// moved, then re-read through the typed listers. A field an adapter must fill and no caller may
/// rely on is not an extension point, it is a bill. Anything that needs a manifest reads it, where
/// the answer is fresh at the moment of asking rather than at the moment of an event.
/// </para>
/// </summary>
public sealed record ResourceEvent
{
    public required WatchEventType Type { get; init; }
    public required ResourceRef Resource { get; init; }
}
