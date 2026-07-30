namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// A host port handed to the cluster at create time, so something inside it can be reached from the
/// machine it runs on.
/// <para>
/// Create time is the only time: the nodes are containers, and a container's published ports are fixed
/// when it starts. Forgetting one means recreating the cluster, which is why the create form asks for
/// these up front rather than offering them later.
/// </para>
/// </summary>
/// <param name="HostPort">The port on this machine. Required — unlike a container's, it cannot be left
/// to the runtime to pick, because the point is knowing where to browse.</param>
/// <param name="ContainerPort">The port on the node.</param>
/// <param name="Protocol">Transport protocol, "tcp" or "udp" — same spelling as <c>PortBinding</c>.</param>
/// <param name="ListenAddress">Which host address to bind, or null for the tool's default. Worth
/// having: the default binds every interface, and a cluster on a laptop in a café should not.</param>
public sealed record ClusterPortMapping(
    int HostPort,
    int ContainerPort,
    string Protocol = "tcp",
    string? ListenAddress = null);
