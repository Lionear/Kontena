namespace Kontena.Core.Orchestration;

/// <summary>
/// A live port-forward tunnel from a local port to a pod/service port. Dispose to tear it down.
/// The neutral handle for the OAL equivalent of <c>kubectl port-forward</c>.
/// </summary>
public interface IPortForward : IAsyncDisposable
{
    /// <summary>The local port the tunnel is listening on.</summary>
    int LocalPort { get; }

    /// <summary>The remote (pod/service) port traffic is forwarded to.</summary>
    int RemotePort { get; }

    /// <summary>Whether the tunnel is currently established.</summary>
    bool IsActive { get; }
}
