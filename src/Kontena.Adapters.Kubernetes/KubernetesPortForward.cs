using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using k8s;
using Kontena.Sdk.Orchestration;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// A live port-forward tunnel to a pod (KON-97) — the OAL equivalent of <c>kubectl port-forward</c>.
/// <para>
/// Kubernetes does not hand out a socket you can simply hook up: it multiplexes the connection over
/// a WebSocket, pairing a data channel with an error channel per forwarded port. So this listens on
/// a local TCP port and pumps bytes between each accepted connection and its own multiplexed stream.
/// One WebSocket per local connection keeps the pairing simple and means one dead connection cannot
/// disturb the others.
/// </para>
/// </summary>
internal sealed class KubernetesPortForward : IPortForward
{
    private readonly IKubernetes _client;
    private readonly string _pod;
    private readonly string? _namespace;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private KubernetesPortForward(IKubernetes client, string pod, string? ns, TcpListener listener, int localPort, int remotePort)
    {
        _client = client;
        _pod = pod;
        _namespace = ns;
        _listener = listener;
        LocalPort = localPort;
        RemotePort = remotePort;
        IsActive = true;
    }

    public int LocalPort { get; }
    public int RemotePort { get; }
    public bool IsActive { get; private set; }

    public event Action<string>? Closed;

    /// <summary>
    /// Start listening. <paramref name="localPort"/> null (or 0) lets the OS pick a free port, which
    /// is the safer default — a fixed port that is already taken would otherwise fail the whole
    /// forward.
    /// </summary>
    public static Task<IPortForward> StartAsync(
        IKubernetes client, string pod, string? ns, int remotePort, int? localPort, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort ?? 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                localPort is > 0
                    ? $"Local port {localPort} is not available: {ex.Message}"
                    : $"Could not open a local port: {ex.Message}",
                ex);
        }

        var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        var forward = new KubernetesPortForward(client, pod, ns, listener, bound, remotePort);

        // Accept in the background; the handle is live as soon as the listener is bound.
        _ = forward.AcceptLoopAsync();
        return Task.FromResult<IPortForward>(forward);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposed or cancelled — the tunnel is finished.
                Drop(_cts.IsCancellationRequested ? null : $"The local listener stopped: {ex.Message}");
                return;
            }

            _ = ForwardAsync(connection);
        }
    }

    private async Task ForwardAsync(TcpClient connection)
    {
        using (connection)
        {
            WebSocket socket;
            try
            {
                socket = await _client.WebSocketNamespacedPodPortForwardAsync(
                    _pod, _namespace, [RemotePort], "v4.channel.k8s.io", cancellationToken: _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Failing to *open* the channel is the tunnel itself being gone — the pod was deleted
                // or restarted, the token expired, RBAC changed. Keeping the listener up after that
                // would leave a local port that accepts connections and can never serve them, which
                // is the failure this reports (KON-102). A connection that breaks after it was
                // established is handled below and only costs that one connection.
                Drop($"The cluster refused a new connection: {ex.Message}");
                return;
            }

            using (socket)
            {
                try
                {
                    using var demuxer = new StreamDemuxer(socket, StreamType.PortForward);
                    demuxer.Start();

                    // Channel 0 carries data for the first (here: only) forwarded port; channel 1 is its
                    // error channel, which we leave to the demuxer.
                    //
                    // Read and write need separate half-streams. A single duplex stream — GetStream(0, 0)
                    // — serves exactly one request and then goes quiet, so an HTTP client reusing the
                    // connection hangs on its second request. Exec has the same constraint.
                    byte? dataChannel = 0;
                    await using var fromPod = demuxer.GetStream(dataChannel, null);
                    await using var toPod = demuxer.GetStream(null, dataChannel);
                    var local = connection.GetStream();

                    // Pump both directions until either end closes.
                    var upstream = local.CopyToAsync(toPod, _cts.Token);
                    var downstream = fromPod.CopyToAsync(local, _cts.Token);
                    await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A connection breaking after it was established (socket reset, pod restarted
                    // mid-request) closes that connection; the listener stays up so the next attempt
                    // can succeed — and if the tunnel really is gone, that attempt says so above.
                }
            }
        }
    }

    /// <summary>
    /// End the tunnel from the inside. <paramref name="reason"/> null means the caller disposed us,
    /// which is a stop rather than a drop and must stay silent — see <see cref="IPortForward.Closed"/>.
    /// </summary>
    private void Drop(string? reason)
    {
        if (!IsActive)
            return;

        IsActive = false;
        _listener.Stop();

        // Free the local port too: a listener that outlives its tunnel accepts connections it can
        // never serve, and holds the port against the reconnect that would fix it.
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();

        if (reason is not null)
            Closed?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        Drop(null);
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
