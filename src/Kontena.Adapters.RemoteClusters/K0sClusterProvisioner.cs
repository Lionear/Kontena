using System.Runtime.CompilerServices;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.RemoteClusters;

/// <summary>
/// Installs Kubernetes on machines you already have, with <c>k0sctl</c> (KON-236).
/// <para>
/// First of the three distributions on purpose. One <c>k0sctl.yaml</c> describes the whole cluster
/// where kubeadm needs a decision per node, the network is part of that same document, and what comes
/// out ships Autopilot — so the cluster is one KON-221 can drive the moment it exists. Most coverage
/// for the least building.
/// </para>
/// <para>
/// It registers nothing, like its local siblings: k0sctl writes a kubeconfig and the discovery that
/// already exists turns that into a backend.
/// </para>
/// </summary>
public sealed class K0sClusterProvisioner(IToolRunner runner, ManagedToolStore? store = null)
    : IRemoteClusterProvisioner
{
    /// <summary>The provisioner id, matching what a spec was built for.</summary>
    public const string Id = "k0s";

    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();

    public string Provisioner => Id;

    public string DisplayName => "k0s";

    /// <summary>
    /// Hosts, SSH, a CNI worth choosing, and machines worth checking first. No port mappings, no
    /// ingress label, no runtimes and no start/stop: those are all things a local tool does to
    /// containers it owns, and none of them mean anything on somebody's own machines.
    /// </summary>
    public ProvisionerCapabilities Capabilities { get; } = new()
    {
        NeedsHosts = true,
        Transport = ProvisionerTransport.Ssh,
        SupportsPreflight = true,

        // k0s installs kube-router unless told otherwise, and takes calico. A real choice, so the form
        // should offer it (KON-232).
        ChoosesCni = true,

        MultiNode = true,
        HighAvailability = true,
        KubernetesVersion = true,
    };

    public ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default)
        => new ToolReadinessCheck(runner, _store).CheckAsync(KnownTools.K0sctl, ct);

    /// <summary>
    /// Nothing to offer, deliberately (KON-144, KON-95, KON-226).
    /// <para>
    /// The rule from KON-144 is "ask the tool where the tool can be asked". minikube can be asked and
    /// is; kind cannot, and got a curated list because a node image is a thing that either exists or
    /// does not and there is no way to enumerate one offline. k0s is neither case: the list of k0s
    /// releases exists, and the only places to get it are the network — which is exactly the traffic
    /// KON-95 and KON-226 decided a local-first desktop tool should not generate unasked — or a table
    /// baked into Kontena, which KON-95 rejected outright for making us the source of truth about a
    /// product we are not the vendor of.
    /// </para>
    /// <para>
    /// So: no list, and no guess. Omitting the version tells k0sctl to install the latest stable it
    /// knows of, which is the answer from the tool that is actually going to do the installing, and
    /// <see cref="RemoteClusterSpec.KubernetesVersion"/> stays as the escape hatch for anything
    /// specific — the same shape as kind's node-image field. When KON-226 settles on asking the
    /// cluster's own update channel, this is the method that changes and nothing else.
    /// </para>
    /// </summary>
    public ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(ClusterVersionOptions.None);

    public string Preview(RemoteClusterSpec spec, IClusterCredentials credentials)
        => K0sctlConfig.Write(spec, Ssh(credentials));

    public async IAsyncEnumerable<ToolLine> CreateAsync(
        RemoteClusterSpec spec,
        IClusterCredentials credentials,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Before anything touches the disk. The wizard asks the same question, but this is a public
        // API and a caller that skipped the wizard must not get further than a rejection (KON-431).
        if (spec.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(spec));

        var config = K0sctlConfig.Write(spec, Ssh(credentials));
        var tool = await ManagedTools.ResolveAsync(KnownTools.K0sctl, runner, _store, ct);
        var directory = await WriteConfigAsync(config, ct);

        try
        {
            var path = Path.Combine(directory.FullName, ConfigFileName);

            var invocation = new ToolInvocation(tool, Arguments(path))
            {
                // No timeout: this installs onto several machines over SSH and legitimately takes
                // minutes. The user is watching k0sctl's own output with a cancel next to it.
                Timeout = null,
            };

            await foreach (var line in runner.StreamAsync(invocation, ct))
                yield return line;
        }
        finally
        {
            Discard(directory);
        }
    }

    /// <summary>
    /// The command line, separate so a test can read it without running anything.
    /// <para>
    /// <c>--no-wait</c> is not passed: the point of a rollout is a cluster that works, and returning
    /// before the nodes are up would report success for something still happening.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string configPath) =>
    [
        "apply",
        "--config", configPath,

        // k0sctl asks for confirmation on a terminal. There is no terminal here, and the confirmation
        // already happened in the wizard.
        "--force",

        // Its own kubeconfig goes to the user's, which is what makes the cluster turn up in the
        // switcher without Kontena registering anything.
        "--kubeconfig-out", "-",
    ];

    /// <summary>
    /// k0sctl speaks SSH and nothing else. A talosconfig here is a caller that picked the wrong
    /// provisioner, and saying so beats writing a config with no login in it.
    /// </summary>
    private static SshCredentials Ssh(IClusterCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return credentials as SshCredentials
               ?? throw new ArgumentException(
                   $"k0s installs over SSH, so it needs {nameof(SshCredentials)}; it was given "
                   + $"{credentials.GetType().Name}, which is for {credentials.Transport}.",
                   nameof(credentials));
    }

    /// <summary>
    /// The file inside that directory. A fixed name, not the cluster's: a name is something a caller
    /// hands us, and one that is rooted or holds <c>..</c> makes <see cref="Path.Combine(string,string)"/>
    /// drop the directory and point somewhere else entirely — which the cleanup below would then
    /// delete recursively (KON-431). The directory is unique per run, so the file need not be.
    /// </summary>
    private const string ConfigFileName = "k0sctl.yaml";

    /// <summary>
    /// Writes the config into a directory of its own, and hands that directory back rather than the
    /// file — so the cleanup deletes what this method created instead of whatever a path happens to
    /// have as its parent. No secret is in the file — a key path is not a key (KON-234).
    /// </summary>
    private static async Task<DirectoryInfo> WriteConfigAsync(string config, CancellationToken ct)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"kontena-k0sctl-{Guid.NewGuid():N}"));

        await File.WriteAllTextAsync(Path.Combine(directory.FullName, ConfigFileName), config, ct);

        return directory;
    }

    private static void Discard(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temp file that will not go is the operating system's problem. Failing a create that
            // already succeeded because of it would be the worse answer.
        }
    }
}
