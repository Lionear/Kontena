namespace Kontena.Sdk.Models;

/// <summary>Lifecycle state of a container, normalized across engines.</summary>
public enum ContainerState
{
    Unknown = 0,
    Created,
    Running,
    Paused,
    Restarting,
    Exited,
    Dead,
    Removing,
}

/// <summary>Restart policy for a container.</summary>
public enum RestartPolicy
{
    No = 0,
    OnFailure,
    UnlessStopped,
    Always,
}

/// <summary>Connection state of an engine as seen by Kontena.</summary>
public enum EngineConnectionState
{
    Unknown = 0,
    Connected,
    Disconnected,
    Unauthorized,
}

/// <summary>Which output stream a log line came from.</summary>
public enum LogSource
{
    Stdout = 0,
    Stderr,
}

/// <summary>The kind of resource an engine event refers to.</summary>
public enum ResourceKind
{
    Container = 0,
    Image,
    Volume,
    Network,
}

/// <summary>High-level category of an engine event.</summary>
public enum EngineEventType
{
    Unknown = 0,
    Created,
    Started,
    Stopped,
    Paused,
    Unpaused,
    Died,
    Removed,
    Pulled,
}
