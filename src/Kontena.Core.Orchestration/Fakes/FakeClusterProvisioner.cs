using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// An <see cref="IClusterProvisioner"/> that makes nothing. Lets the pages that create and delete
/// clusters be built and tested on a machine with no kind, no minikube and no container runtime.
/// <para>
/// It keeps a list, streams a few lines and remembers what it was asked for. Deliberately not a
/// simulation: a fake that invented plausible kind output would let a wrong spec pass as long as the
/// story read well.
/// </para>
/// </summary>
public sealed class FakeClusterProvisioner : IClusterProvisioner
{
    private static readonly ExternalTool FakeTool = new("fake", "fake-cluster-tool", ["version"], []);

    private readonly List<LocalCluster> _clusters = [];

    /// <summary>
    /// The id this fake answers to. Settable so a test can stand up two provisioners that are actually
    /// distinct — a merged list built from one object twice proves nothing about merging.
    /// </summary>
    public string Provisioner { get; init; } = "fake";

    public string DisplayName { get; init; } = "Fake";

    public ProvisionerCapabilities Capabilities { get; init; } = new()
    {
        MultiNode = true,
        PortMappings = true,
        IngressReady = true,
        KubernetesVersion = true,
        Runtimes = [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman],
        Resources = true,
        StartStop = true,
    };

    /// <summary>What <see cref="VersionsAsync"/> answers. Empty by default: most tests are not about it.</summary>
    public ClusterVersionOptions Versions { get; init; } = ClusterVersionOptions.None;

    /// <summary>What <see cref="CheckAsync"/> answers. Set it to a missing tool to build the empty state.</summary>
    public ToolReadiness Readiness { get; init; } =
        new(FakeTool, ToolState.Ready, "/fake/bin/fake-cluster-tool", "v1.0.0", false, null);

    /// <summary>Every spec that was created, in order — the point of the fake.</summary>
    public List<LocalClusterSpec> Created { get; } = [];

    /// <summary>Every name that was deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Every name that was started, and every one that was stopped, in order.</summary>
    public List<string> Started { get; } = [];

    public List<string> Stopped { get; } = [];

    /// <summary>Lines <see cref="CreateAsync"/> streams. Replace them to rehearse a specific console.</summary>
    public IReadOnlyList<string> CreateOutput { get; init; } =
        ["Ensuring node image", "Preparing nodes", "Starting control-plane", "Ready"];

    /// <summary>Make the next create fail after streaming its lines, to exercise the error path.</summary>
    public int CreateExitCode { get; init; }

    /// <summary>Seed clusters that already exist.</summary>
    public FakeClusterProvisioner WithCluster(string name, LocalClusterState state = LocalClusterState.Unknown)
    {
        _clusters.Add(new LocalCluster(name, Provisioner, $"{Provisioner}-{name}") { State = state });
        return this;
    }

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Readiness);

    public ValueTask<IReadOnlyList<LocalCluster>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<LocalCluster>>([.. _clusters]);

    public ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Versions);

    public async IAsyncEnumerable<ToolLine> CreateAsync(
        LocalClusterSpec spec,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        LocalClusterName.Validate(spec.Name, nameof(spec));

        Created.Add(spec);

        foreach (var line in CreateOutput)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ToolLine(ToolOutputKind.Out, line);
        }

        await Task.CompletedTask;

        if (CreateExitCode != 0)
            throw new ToolFailedException($"fake create cluster {spec.Name}", CreateExitCode, "fake failure");

        _clusters.Add(new LocalCluster(spec.Name, Provisioner, $"{Provisioner}-{spec.Name}"));
    }

    public ValueTask DeleteAsync(string name, CancellationToken ct = default)
    {
        Deleted.Add(name);
        _clusters.RemoveAll(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ToolLine> StartAsync(
        string name,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Started.Add(name);

        foreach (var line in CreateOutput)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ToolLine(ToolOutputKind.Out, line);
        }

        await Task.CompletedTask;
        Replace(name, LocalClusterState.Running);
    }

    public ValueTask StopAsync(string name, CancellationToken ct = default)
    {
        Stopped.Add(name);
        Replace(name, LocalClusterState.Stopped);
        return ValueTask.CompletedTask;
    }

    private void Replace(string name, LocalClusterState state)
    {
        var index = _clusters.FindIndex(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (index >= 0)
            _clusters[index] = _clusters[index] with { State = state };
    }
}
