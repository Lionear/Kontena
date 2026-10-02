using System.Net;
using System.Text;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The post-create apply (KON-465): what a provisioner leaves to be put on the cluster it just made,
/// and what the page does when that does not work.
/// <para>
/// Served from a loopback listener rather than from the real pinned URL. The point being tested is the
/// step, not Calico's CDN — and a test that reaches the internet fails for reasons that have nothing to
/// do with the code it is about.
/// </para>
/// </summary>
public class LocalClustersPostCreateTests : IDisposable
{
    private const string Manifest = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: pretend-cni
        """;

    private readonly HttpListener _server = new();
    private readonly string _url;

    public LocalClustersPostCreateTests()
    {
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}/cni.yaml";
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = Task.Run(Serve);
    }

    public void Dispose()
    {
        _server.Close();
        GC.SuppressFinalize(this);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _server.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var body = Encoding.UTF8.GetBytes(Manifest);
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static LocalClustersViewModel Page(FakeClusterProvisioner provisioner, IClusterEngine? engine = null) =>
        new([provisioner], new FakeToolRunner(),
            store: new ManagedToolStore(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}")))
        {
            RequestConfirm = request => _ = request.OnConfirm(),
            EngineFor = engine is null ? null : _ => engine,
        };

    private static async Task CreateAsync(LocalClustersViewModel page, string name)
    {
        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);
        page.Form!.Name = name;
        await page.CreateCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_provisioner_with_nothing_to_add_never_reaches_for_a_cluster()
    {
        var page = Page(new FakeClusterProvisioner());

        // No engine handed over: if the step ran anyway it would try to connect to a real API server.
        await CreateAsync(page, "dev");

        Assert.Equal(LocalClustersStage.List, page.Stage);
    }

    [Fact]
    public async Task What_the_provisioner_asked_for_is_fetched_and_applied()
    {
        var engine = new FakeClusterEngine();
        var page = Page(
            new FakeClusterProvisioner { PostCreate = [new ClusterManifest("Pretend CNI 1.2.3", _url)] },
            engine);

        await CreateAsync(page, "dev");

        Assert.Equal(LocalClustersStage.List, page.Stage);
        Assert.Contains(page.Output, line => line.Contains("Applying Pretend CNI 1.2.3", StringComparison.Ordinal));
        Assert.Contains(page.Output, line => line.Contains("pretend-cni", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_fetched_fails_the_create_with_the_cluster_still_there()
    {
        var page = Page(
            new FakeClusterProvisioner
            {
                // A port with nothing behind it: the download fails the way a project's CDN being
                // unreachable would, which is the case this path exists for.
                PostCreate = [new ClusterManifest("Pretend CNI 1.2.3", $"http://127.0.0.1:{FreePort()}/cni.yaml")],
            },
            new FakeClusterEngine());

        await CreateAsync(page, "dev");

        Assert.Equal(LocalClustersStage.Failed, page.Stage);
        Assert.Contains("Pretend CNI 1.2.3", page.Error, StringComparison.Ordinal);

        // The wording that matters: the cluster exists, so "try again" would be the wrong next step.
        Assert.Contains("was created", page.FailureHint, StringComparison.Ordinal);
    }
}
