using System.Runtime.CompilerServices;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Creates local Kubernetes clusters with <c>minikube</c> — a VM or a container per cluster, with more
/// drivers and more knobs than kind, and the one thing kind cannot do: stop a cluster and start it
/// again (KON-77).
/// <para>
/// Like kind, it registers nothing: minikube writes a kubeconfig context named after the profile, and
/// the existing discovery turns that into a backend.
/// </para>
/// </summary>
public sealed class MinikubeClusterProvisioner(IToolRunner runner, ManagedToolStore? store = null)
    : IClusterProvisioner
{
    /// <summary>The provisioner id, matching <see cref="LocalCluster.Provisioner"/>.</summary>
    public const string Id = "minikube";

    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();

    public string Provisioner => Id;

    public string DisplayName => "minikube";

    /// <summary>
    /// Resources and start/stop are what this adds over kind. No ingress preset: minikube installs an
    /// ingress controller through an addon rather than a create-time label, and offering the kind
    /// wording here would promise something else than it does.
    /// </summary>
    public ProvisionerCapabilities Capabilities { get; } = new()
    {
        MultiNode = true,
        HighAvailability = false,
        PortMappings = true,
        IngressReady = false,
        KubernetesVersion = true,
        Runtimes = [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman, LocalClusterRuntime.Kvm2],
        Resources = true,
        StartStop = true,
    };

    /// <summary>The kubeconfig context minikube writes for a profile — the profile name itself.</summary>
    public static string ContextFor(string name) => name;

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default)
        => new ToolReadinessCheck(runner, _store).CheckAsync(KnownTools.Minikube, ct);

    public async ValueTask<IReadOnlyList<LocalCluster>> ListAsync(CancellationToken ct = default)
    {
        ToolResult result;
        try
        {
            var tool = await ManagedTools.ResolveAsync(KnownTools.Minikube, runner, _store, ct);
            result = await runner.RunAsync(new ToolInvocation(tool, MinikubeArguments.List()), ct);
        }
        catch (ToolNotFoundException)
        {
            return [];
        }

        // A non-zero exit with no profiles is minikube's normal way of saying "none" on some versions,
        // so the output is parsed either way and an unreadable one yields nothing.
        return MinikubeProfiles.Parse(result.StandardOutput, Id);
    }

    /// <summary>
    /// minikube's own answer to what it supports, narrowed by <see cref="MinikubeVersions"/>. Local and
    /// quick — it reads a table compiled into the binary, so this is not a network call — and it stays
    /// right across tool updates, which a list of ours would not (KON-144).
    /// </summary>
    public async ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var tool = await ManagedTools.ResolveAsync(KnownTools.Minikube, runner, _store, ct);
            var result = await runner.RunAsync(new ToolInvocation(tool, MinikubeArguments.Versions()), ct);

            return MinikubeVersions.Parse(result.StandardOutput);
        }
        catch (ToolNotFoundException)
        {
            return ClusterVersionOptions.None;
        }
    }

    public async IAsyncEnumerable<ToolLine> CreateAsync(
        LocalClusterSpec spec,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        LocalClusterName.Validate(spec.Name, nameof(spec));

        var tool = await ManagedTools.ResolveAsync(KnownTools.Minikube, runner, _store, ct);

        // No timeout: downloading a base image and booting a VM takes minutes, and the user is
        // watching minikube's own narration with a cancel button next to it.
        var invocation = new ToolInvocation(tool, MinikubeArguments.Create(spec)) { Timeout = null };

        await foreach (var line in runner.StreamAsync(invocation, ct))
            yield return line;
    }

    public async IAsyncEnumerable<ToolLine> StartAsync(
        string name,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LocalClusterName.Validate(name, nameof(name));

        var tool = await ManagedTools.ResolveAsync(KnownTools.Minikube, runner, _store, ct);
        var invocation = new ToolInvocation(tool, MinikubeArguments.Start(name)) { Timeout = null };

        await foreach (var line in runner.StreamAsync(invocation, ct))
            yield return line;
    }

    public ValueTask StopAsync(string name, CancellationToken ct = default)
        => RunAsync(name, MinikubeArguments.Stop(name), ct);

    public ValueTask DeleteAsync(string name, CancellationToken ct = default)
        => RunAsync(name, MinikubeArguments.Delete(name), ct);

    private async ValueTask RunAsync(string name, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        LocalClusterName.Validate(name, nameof(name));

        var tool = await ManagedTools.ResolveAsync(KnownTools.Minikube, runner, _store, ct);

        // Stopping a multi-node profile is not instant, and the default two minutes is a hang, not a
        // wait, for a cluster that is genuinely shutting down. Five is the compromise.
        var invocation = new ToolInvocation(tool, arguments) { Timeout = TimeSpan.FromMinutes(5) };

        var result = await runner.RunAsync(invocation, ct);
        if (!result.Ok)
            throw new ToolFailedException(invocation.CommandLine, result.ExitCode, result.Complaint);
    }
}
