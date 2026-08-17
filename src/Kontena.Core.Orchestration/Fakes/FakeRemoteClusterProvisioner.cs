using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// A remote provisioner that installs nothing (KON-236).
/// <para>
/// What it is for: the wizard and the screenshots. Every other step of this epic can be exercised
/// without machines — a host table is text, a preflight has a fake probe — but the last one ends in
/// "install Kubernetes on five computers", and neither a demo nor a screenshot can do that. This
/// streams a rollout that looks like one and touches nothing.
/// </para>
/// <para>
/// It is also the honest test double for anything that consumes <see cref="IRemoteClusterProvisioner"/>
/// without being about k0sctl: it records the specs it was handed, which is usually the assertion.
/// </para>
/// </summary>
public sealed class FakeRemoteClusterProvisioner : IRemoteClusterProvisioner
{
    private const string FakeTool = "fake-cluster-tool";

    public string Provisioner { get; init; } = "fake-remote";

    public string DisplayName { get; init; } = "Fake (remote)";

    public ProvisionerCapabilities Capabilities { get; init; } = new()
    {
        NeedsHosts = true,
        Transport = ProvisionerTransport.Ssh,
        SupportsPreflight = true,
        ChoosesCni = true,
        MultiNode = true,
        HighAvailability = true,
        KubernetesVersion = true,
    };

    /// <summary>What <see cref="VersionsAsync"/> answers. None by default, as the real one does.</summary>
    public ClusterVersionOptions Versions { get; init; } = ClusterVersionOptions.None;

    /// <summary>What <see cref="CheckAsync"/> answers. Set it to a missing tool to build the empty state.</summary>
    public ToolReadiness Readiness { get; init; } =
        new(new ExternalTool(FakeTool, FakeTool, ["version"], []), ToolState.Ready, "/fake/bin/k0sctl", "v0.19.2", false, null);

    /// <summary>Every spec it was asked to install, in order — usually the point of the fake.</summary>
    public List<RemoteClusterSpec> Created { get; } = [];

    /// <summary>The credentials it was handed with them, so a test can check they arrived.</summary>
    public List<IClusterCredentials> Credentials { get; } = [];

    /// <summary>How long to wait between lines, so a demo reads as a rollout rather than a paste.</summary>
    public TimeSpan LineDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Made to fail after this many lines, for the failure screenshot. Null runs clean.</summary>
    public int? FailAfter { get; init; }

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Readiness);

    public ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Versions);

    /// <summary>The config a real one would write, so the preview is not empty in a demo.</summary>
    public string Preview(RemoteClusterSpec spec, IClusterCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return string.Join('\n',
            "apiVersion: k0sctl.k0sproject.io/v1beta1",
            "kind: Cluster",
            "metadata:",
            $"  name: {spec.Name}",
            "spec:",
            "  hosts:",
            string.Join('\n', spec.Hosts.Select(h =>
                $"    - ssh: {{address: {h.Address}}}\n      role: {(h.Role == ClusterHostRole.Controller ? "controller" : "worker")}")));
    }

    public async IAsyncEnumerable<ToolLine> CreateAsync(
        RemoteClusterSpec spec,
        IClusterCredentials credentials,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        Created.Add(spec);
        Credentials.Add(credentials);

        var written = 0;

        foreach (var line in Script(spec))
        {
            ct.ThrowIfCancellationRequested();

            if (LineDelay > TimeSpan.Zero)
                await Task.Delay(LineDelay, ct);

            yield return new ToolLine(ToolOutputKind.Out, line);

            if (++written == FailAfter)
            {
                // Thrown at the end of enumeration, exactly as the real one does — a caller that only
                // renders lines must not be able to show a failure as success.
                throw new ToolFailedException($"{FakeTool} apply", 1, "the fake was asked to fail");
            }
        }
    }

    /// <summary>
    /// A rollout in k0sctl's own shape: connect, gather facts, controllers first, then workers.
    /// Ordered that way because a screenshot of a half-finished rollout should show a believable half.
    /// </summary>
    private static IEnumerable<string> Script(RemoteClusterSpec spec)
    {
        yield return "⡿ Connecting to hosts";

        foreach (var host in spec.Hosts)
            yield return $"✔ {host.Address}: connected";

        yield return "⡿ Detecting host operating systems";

        foreach (var host in spec.Hosts)
            yield return $"✔ {host.Address}: is running Ubuntu 24.04.1 LTS";

        yield return "⡿ Validating hosts";
        yield return "⡿ Gathering k0s facts";

        foreach (var host in spec.Hosts.Where(h => h.Role == ClusterHostRole.Controller))
            yield return $"✔ {host.Address}: installing k0s controller";

        foreach (var host in spec.Hosts.Where(h => h.Role == ClusterHostRole.Worker))
            yield return $"✔ {host.Address}: installing k0s worker";

        yield return "⡿ Waiting for the nodes to become ready";
        yield return $"✔ k0s cluster {spec.Name} is ready";
    }
}
