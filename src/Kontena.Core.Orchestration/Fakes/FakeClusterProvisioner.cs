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

    public string Provisioner => "fake";

    public string DisplayName => "Fake";

    public ProvisionerCapabilities Capabilities { get; init; } = new()
    {
        MultiNode = true,
        PortMappings = true,
        IngressReady = true,
        KubernetesVersion = true,
    };

    /// <summary>What <see cref="CheckAsync"/> answers. Set it to a missing tool to build the empty state.</summary>
    public ToolReadiness Readiness { get; init; } =
        new(FakeTool, ToolState.Ready, "/fake/bin/fake-cluster-tool", "v1.0.0", false, null);

    /// <summary>Every spec that was created, in order — the point of the fake.</summary>
    public List<LocalClusterSpec> Created { get; } = [];

    /// <summary>Every name that was deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Lines <see cref="CreateAsync"/> streams. Replace them to rehearse a specific console.</summary>
    public IReadOnlyList<string> CreateOutput { get; init; } =
        ["Ensuring node image", "Preparing nodes", "Starting control-plane", "Ready"];

    /// <summary>Make the next create fail after streaming its lines, to exercise the error path.</summary>
    public int CreateExitCode { get; init; }

    /// <summary>Seed clusters that already exist.</summary>
    public FakeClusterProvisioner WithCluster(string name)
    {
        _clusters.Add(new LocalCluster(name, Provisioner, $"fake-{name}"));
        return this;
    }

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Readiness);

    public ValueTask<IReadOnlyList<LocalCluster>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<LocalCluster>>([.. _clusters]);

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

        _clusters.Add(new LocalCluster(spec.Name, Provisioner, $"fake-{spec.Name}"));
    }

    public ValueTask DeleteAsync(string name, CancellationToken ct = default)
    {
        Deleted.Add(name);
        _clusters.RemoveAll(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }
}
