namespace Kontena.Core.Orchestration.Models;

/// <summary>How a Service is exposed.</summary>
public enum ServiceType
{
    ClusterIp,
    NodePort,
    LoadBalancer,
    ExternalName,
    Headless,
}

/// <summary>One port a Service publishes.</summary>
public readonly record struct ServicePort(string Name, int Port, int TargetPort, int? NodePort, string Protocol);

/// <summary>A neutral Service view for the network grid.</summary>
public sealed record Service
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public ServiceType Type { get; init; } = ServiceType.ClusterIp;

    /// <summary>Cluster IP, or "None" for headless services.</summary>
    public string ClusterIp { get; init; } = string.Empty;

    /// <summary>External IP / hostname for LoadBalancer services, when assigned.</summary>
    public string ExternalIp { get; init; } = string.Empty;

    public IReadOnlyList<ServicePort> Ports { get; init; } = [];

    /// <summary>Selector that maps the service to pods.</summary>
    public IReadOnlyDictionary<string, string> Selector { get; init; } =
        new Dictionary<string, string>();

    public TimeSpan Age { get; init; }
}

/// <summary>One host/path routing rule of an Ingress.</summary>
public readonly record struct IngressRule(string Host, string Path, string ServiceName, int ServicePort);

/// <summary>A neutral Ingress view.</summary>
public sealed record Ingress
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>IngressClass, e.g. "nginx", "traefik".</summary>
    public string Class { get; init; } = string.Empty;

    public IReadOnlyList<IngressRule> Rules { get; init; } = [];

    /// <summary>Load-balancer addresses the ingress is reachable at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Hosts covered by TLS.</summary>
    public IReadOnlyList<string> TlsHosts { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>Bind/lifecycle phase of a PersistentVolumeClaim.</summary>
public enum PvcPhase
{
    Pending,
    Bound,
    Lost,
}

/// <summary>A neutral PersistentVolumeClaim view for the storage grid.</summary>
public sealed record PersistentVolumeClaim
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public PvcPhase Phase { get; init; } = PvcPhase.Pending;

    /// <summary>Bound volume name, when bound.</summary>
    public string Volume { get; init; } = string.Empty;

    /// <summary>Requested capacity in bytes.</summary>
    public long CapacityBytes { get; init; }

    /// <summary>StorageClass backing the claim.</summary>
    public string StorageClass { get; init; } = string.Empty;

    /// <summary>Access modes, e.g. "RWO", "RWX".</summary>
    public IReadOnlyList<string> AccessModes { get; init; } = [];

    public TimeSpan Age { get; init; }
}
