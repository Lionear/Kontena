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

    /// <summary>
    /// Raised at most once, when the tunnel stops on its own — the pod went away, the connection was
    /// refused, the apiserver dropped it. The argument is a sentence for the user, not a code.
    ///
    /// <para>Implementations must <b>not</b> raise this when the caller disposes the handle: that is
    /// a stop, not a drop, and the caller already knows. Only the adapter knows when a tunnel ends
    /// by itself, which is exactly why it must say so rather than let the UI poll for it (KON-102).</para>
    ///
    /// <para>Raised on whichever thread noticed; subscribers marshal it themselves.</para>
    /// </summary>
    event Action<string>? Closed;
}
