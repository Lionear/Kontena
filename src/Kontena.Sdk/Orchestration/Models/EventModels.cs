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
/// One item from a <c>WatchAsync</c> informer stream: what happened, to which resource, and
/// the resource's live manifest (YAML) at that revision. Generic over kind via
/// <see cref="ResourceRef"/> + <see cref="GroupVersionKind"/>, so a single stream type serves
/// every resource — including CRDs.
/// </summary>
public sealed record ResourceEvent
{
    public required WatchEventType Type { get; init; }
    public required ResourceRef Resource { get; init; }

    /// <summary>The resource's manifest (YAML) at this revision, when the adapter supplies it.</summary>
    public string? Manifest { get; init; }
}
