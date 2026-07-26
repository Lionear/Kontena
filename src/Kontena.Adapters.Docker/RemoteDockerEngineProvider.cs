using Kontena.Core;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.Adapters.Docker;

/// <summary>
/// A Docker engine on another host, as its own entry in the switcher (KON-46).
/// <para>
/// One provider per configured remote, so a remote engine is a backend like any other — the same CEAL
/// path, the same pages, the same switcher. Nothing above this layer needs to know that the socket is
/// somewhere else.
/// </para>
/// </summary>
public sealed class RemoteDockerEngineProvider : IBackendProvider
{
    private readonly RemoteEngine _remote;

    public RemoteDockerEngineProvider(RemoteEngine remote) => _remote = remote;

    public string Backend => _remote.Backend;
    public string DisplayName => _remote.Name;

    /// <summary>
    /// "R" rather than Docker's "D": in a switcher holding both, which entry is the one on the server is
    /// the thing worth being able to tell at a glance.
    /// </summary>
    public string Chip => "R";

    public BackendKind Kind => BackendKind.Engine;

    public IBackend CreateBackend()
    {
        if (_remote.Problem is { } problem)
            throw new EngineException(problem);

        return _remote.Transport switch
        {
            RemoteEngineTransport.Ssh => SshEngineFactory.Create(_remote),
            _ => new DockerEngine(
                new Uri($"tcp://{_remote.Host}:{_remote.Port ?? RemoteEngine.DefaultTlsPort}"),
                _remote.Backend,
                _remote.Name,
                _remote.CertificateDirectory),
        };
    }
}

/// <summary>
/// Opens the tunnel for an SSH remote and returns an engine speaking through it.
/// <para>
/// Blocking, because <see cref="IBackendProvider.CreateBackend"/> is: the registry probes backends off the
/// UI thread, so a host that is asleep costs the probe its timeout rather than freezing the window. Ten
/// seconds is that budget — long enough for a real connection, short enough that an unreachable remote
/// does not hold up the switcher.
/// </para>
/// </summary>
internal static class SshEngineFactory
{
    public static IBackend Create(RemoteEngine remote)
    {
        var tunnel = SshTunnel.OpenAsync(remote, TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();

        return new DockerEngine(tunnel.Endpoint, remote.Backend, remote.Name, attached: tunnel);
    }
}
