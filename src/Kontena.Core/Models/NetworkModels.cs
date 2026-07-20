namespace Kontena.Core.Models;

/// <summary>Engine-neutral summary of a network.</summary>
public sealed record NetworkSummary
{
    /// <summary>Engine-assigned id.</summary>
    public required string Id { get; init; }

    /// <summary>Network name.</summary>
    public required string Name { get; init; }

    /// <summary>Driver, e.g. "bridge", "host", "overlay", "null".</summary>
    public string Driver { get; init; } = "bridge";

    /// <summary>Scope, e.g. "local" or "swarm".</summary>
    public string Scope { get; init; } = "local";

    /// <summary>Subnet in CIDR form when applicable.</summary>
    public string? Subnet { get; init; }

    /// <summary>Names of containers attached to this network.</summary>
    public IReadOnlyList<string> AttachedContainers { get; init; } = [];

    /// <summary>True for engine-provided networks that cannot be removed.</summary>
    public bool IsBuiltIn { get; init; }
}

/// <summary>Request to create a network.</summary>
public sealed record CreateNetworkRequest
{
    /// <summary>Desired network name.</summary>
    public required string Name { get; init; }

    /// <summary>Driver to use.</summary>
    public string Driver { get; init; } = "bridge";

    /// <summary>Optional subnet in CIDR form.</summary>
    public string? Subnet { get; init; }
}
