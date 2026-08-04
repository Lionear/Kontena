using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// A remote gets longer to answer a probe than a local socket does (KON-327).
/// <para>
/// The two budgets used to contradict each other: the tunnel was given ten seconds — "long enough for
/// a real connection", said the comment — and the registry shot it down after two. TCP plus key
/// exchange plus auth to a host over a WAN costs more than that, so an SSH remote could not pass a
/// probe at all. Settings, which tested with no timeout whatsoever, reported the same host as
/// connected. Kontena contradicted itself on one screen.
/// </para>
/// </summary>
public class RemoteProbeBudgetTests
{
    private static IBackendProvider Remote(RemoteEngineTransport transport) =>
        new RemoteDockerEngineProvider(new RemoteEngine(
            Id: "r1", Name: "Server", Transport: transport, Host: "example.test", User: "rick",
            CertificateDirectory: transport == RemoteEngineTransport.Tcp ? "/certs" : null));

    [Theory]
    [InlineData(RemoteEngineTransport.Ssh)]
    [InlineData(RemoteEngineTransport.Tcp)]
    public void A_remote_gets_more_than_the_local_default(RemoteEngineTransport transport)
    {
        IBackendProvider local = new LocalProvider();

        Assert.True(
            Remote(transport).ProbeTimeout > local.ProbeTimeout,
            "a remote crosses a network before it can answer, and was being cut off before it could");
    }

    /// <summary>Stands in for Docker or Podman on this machine: it takes the interface default.</summary>
    private sealed class LocalProvider : IBackendProvider
    {
        public string Backend => "local";
        public string DisplayName => "Local";
        public string Chip => "L";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => throw new NotSupportedException("never created here");
    }
}
