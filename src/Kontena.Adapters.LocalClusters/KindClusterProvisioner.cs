using System.Runtime.CompilerServices;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Creates local Kubernetes clusters with <c>kind</c> — nodes as containers on the runtime that is
/// already there, which is why it is the first provisioner: someone running Kontena has a container
/// runtime by definition.
/// <para>
/// It registers nothing. kind writes its own <c>kind-&lt;name&gt;</c> context into the kubeconfig, and
/// the existing discovery turns that into a backend; this class only makes and unmakes.
/// </para>
/// </summary>
public sealed class KindClusterProvisioner(IToolRunner runner, ManagedToolStore? store = null)
    : IClusterProvisioner
{
    /// <summary>The provisioner id, matching <see cref="LocalCluster.Provisioner"/>.</summary>
    public const string Id = "kind";

    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();

    public string Provisioner => Id;

    public string DisplayName => "kind";

    /// <summary>
    /// No start/stop and no resources: kind has no such command, and its nodes are containers that take
    /// what the host has. Stopping them behind its back is not the same thing — the cluster comes back
    /// with a control plane that believes no time passed.
    /// </summary>
    public ProvisionerCapabilities Capabilities { get; } = new()
    {
        MultiNode = true,
        HighAvailability = true,
        PortMappings = true,
        IngressReady = true,
        KubernetesVersion = true,
        Runtimes = [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman],

        // No resources: the nodes are containers and take what the host has.
        Resources = false,
        StartStop = false,
    };

    /// <summary>The kubeconfig context kind writes for a cluster of this name.</summary>
    public static string ContextFor(string name) => $"kind-{name}";

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default)
        => new ToolReadinessCheck(runner, _store).CheckAsync(KnownTools.Kind, ct);

    public async ValueTask<IReadOnlyList<LocalCluster>> ListAsync(CancellationToken ct = default)
    {
        ToolResult result;
        try
        {
            var tool = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, _store, ct);
            result = await runner.RunAsync(new ToolInvocation(tool, KindArguments.List()), ct);
        }
        catch (ToolNotFoundException)
        {
            // No kind means no kind clusters. The settings page says the tool is missing and offers to
            // install it; an empty list here is the honest answer to what was asked.
            return [];
        }

        if (!result.Ok)
            return [];

        var names = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // kind says "No kind clusters found." when there are none. It goes to stderr on the
            // versions we drive, but it has moved between streams before, and treating it as a
            // cluster name would put a sentence in the switcher.
            .Where(line => !line.StartsWith("No kind clusters", StringComparison.Ordinal));

        var clusters = new List<LocalCluster>();
        foreach (var name in names)
            clusters.Add(new LocalCluster(name, Id, ContextFor(name)) { Nodes = await NodesAsync(name, ct) });

        return clusters;
    }

    /// <summary>
    /// One cluster's node containers. A failure yields nothing rather than throwing: the cluster is
    /// listed either way, and losing the whole list over a count would be the wrong trade.
    /// </summary>
    private async ValueTask<IReadOnlyList<string>> NodesAsync(string name, CancellationToken ct)
    {
        try
        {
            var tool = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, _store, ct);
            var result = await runner.RunAsync(new ToolInvocation(tool, KindArguments.Nodes(name)), ct);

            return result.Ok
                ? result.StandardOutput.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (ToolNotFoundException)
        {
            return [];
        }
    }

    public async IAsyncEnumerable<ToolLine> CreateAsync(
        LocalClusterSpec spec,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        LocalClusterName.Validate(spec.Name, nameof(spec));

        var tool = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, _store, ct);
        var configPath = KindConfig.Needed(spec) ? await WriteConfigAsync(spec, ct) : null;

        try
        {
            var invocation = new ToolInvocation(tool, KindArguments.Create(spec, configPath))
            {
                Environment = Environment(spec.Runtime),

                // No timeout on purpose: pulling a node image over a slow connection is not a hang,
                // and the user is watching the output with a cancel button next to it.
                Timeout = null,
            };

            await foreach (var line in runner.StreamAsync(invocation, ct))
                yield return line;
        }
        finally
        {
            Discard(configPath);
        }
    }

    public async ValueTask DeleteAsync(string name, CancellationToken ct = default)
    {
        LocalClusterName.Validate(name, nameof(name));

        var tool = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, _store, ct);
        var invocation = new ToolInvocation(tool, KindArguments.Delete(name));

        var result = await runner.RunAsync(invocation, ct);
        if (!result.Ok)
            throw new ToolFailedException(invocation.CommandLine, result.ExitCode, result.Complaint);
    }

    /// <summary>
    /// kind has no stopped state. Its clusters are containers that the tool expects to be running, and
    /// stopping them behind its back leaves a control plane that comes back believing no time passed —
    /// so this refuses rather than pretending, and <see cref="Capabilities"/> keeps the UI from asking.
    /// </summary>
    public IAsyncEnumerable<ToolLine> StartAsync(string name, CancellationToken ct = default)
        => throw new NotSupportedException("kind cannot stop or start a cluster; delete and create it again.");

    /// <inheritdoc cref="StartAsync"/>
    public ValueTask StopAsync(string name, CancellationToken ct = default)
        => throw new NotSupportedException("kind cannot stop or start a cluster; delete and create it again.");

    /// <summary>
    /// How kind is told which runtime to use. Podman is opt-in through kind's own variable; Docker
    /// clears it rather than setting it, because clearing is the one value that is certainly
    /// understood by every kind version — and it also undoes an inherited setting from the user's
    /// shell, which is the case that would otherwise silently build the cluster somewhere else.
    /// </summary>
    private static Dictionary<string, string?> Environment(LocalClusterRuntime runtime)
        => runtime switch
        {
            LocalClusterRuntime.Podman => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KIND_EXPERIMENTAL_PROVIDER"] = "podman",
            },
            LocalClusterRuntime.Docker => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KIND_EXPERIMENTAL_PROVIDER"] = null,
            },
            _ => new Dictionary<string, string?>(StringComparer.Ordinal),
        };

    private static async ValueTask<string> WriteConfigAsync(LocalClusterSpec spec, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-kind-{spec.Name}-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, KindConfig.Write(spec), ct);
        return path;
    }

    /// <summary>
    /// Remove the temp config. A failure here is swallowed: the cluster either exists or it does not,
    /// and a leftover file in the temp directory is not worth turning a finished create into an error.
    /// </summary>
    private static void Discard(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
