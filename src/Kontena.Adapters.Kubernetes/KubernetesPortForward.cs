using System.Net;
using System.Net.Sockets;
using k8s;
using Kontena.Core.Orchestration;

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
            catch (Exception)
            {
                // Disposed or cancelled — the tunnel is finished.
                IsActive = false;
                return;
            }

            _ = ForwardAsync(connection);
        }
    }

    private async Task ForwardAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                using var socket = await _client.WebSocketNamespacedPodPortForwardAsync(
                    _pod, _namespace, [RemotePort], "v4.channel.k8s.io", cancellationToken: _cts.Token)
                    .ConfigureAwait(false);

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
                // A single connection failing (pod restarted, RBAC, socket reset) closes that
                // connection; the listener stays up so the next attempt can succeed.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsActive = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _cts.Dispose();
    }
}
