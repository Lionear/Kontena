using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// Registers one containerd namespace as a backend, alongside Docker and Podman — the same
/// "one provider per entry" shape <c>KubernetesClusterProvider</c> already uses for kube-contexts
/// (KON-141); <see cref="IBackendProvider"/>'s own docs call this out as a provider surfacing several
/// entries, not a special case this plugin invents.
/// </summary>
public sealed class NerdctlEngineProvider : IBackendProvider
{
    /// <summary>
    /// containerd's own dumping ground for a shared-daemon Docker's containers. Never surfaced as a
    /// namespace of its own: those containers already appear under the Docker backend, and listing
    /// them again here would show a Docker user every container twice.
    /// </summary>
    private const string DockerSharedNamespace = "moby";

    /// <summary>
    /// How long <see cref="DiscoverAll"/> waits for <c>namespace ls</c> before giving up on it. This
    /// runs synchronously at startup, before there is any window to show progress in — an unresponsive
    /// containerd socket or a waking lima VM cannot be allowed to hold that up for
    /// <c>ToolRunner</c>'s ordinary two-minute default. Five seconds is generous for what, on a working
    /// install, answers instantly; a local namespace list that cannot in that time is not going to,
    /// however much longer it is given.
    /// </summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly string _namespace;

    /// <summary>Handed to the <see cref="NerdctlCli"/> each <see cref="CreateBackend"/> builds — the
    /// same runner every namespace's engine shells out through.</summary>
    private readonly IToolRunner _runner;

    public NerdctlEngineProvider(string @namespace, IToolRunner runner)
    {
        _namespace = @namespace;
        _runner = runner;
    }

    /// <summary>Namespace this instance speaks for — every command <see cref="CreateBackend"/> will
    /// eventually issue needs it, the same way a kube-context needs its name.</summary>
    public string Namespace => _namespace;

    public string Backend => $"nerdctl:{_namespace}";

    /// <summary><c>default</c> reads as plain "nerdctl" — it is the only namespace most installs ever
    /// have, so naming it after itself would be noise. Anything else names the namespace, since by
    /// definition there is more than one and the switcher needs to tell them apart.</summary>
    public string DisplayName => _namespace == "default" ? "nerdctl" : $"nerdctl ({_namespace})";

    public string Chip => "N";

    public BackendKind Kind => BackendKind.Engine;

    // ChipStyle is left at IBackendProvider's own default (null): no 16px glyph for nerdctl has been
    // approved in DesignSystem.md yet, so the switcher falls back to the "N" letter badge.

    /// <summary>
    /// One <see cref="NerdctlEngine"/> per call, talking to this instance's namespace through a fresh
    /// <see cref="NerdctlCli"/> — cheap enough (no connection to open) that there is no reason to cache
    /// and share one across probes the way <c>DockerEngine</c> must for its client.
    /// </summary>
    public IBackend CreateBackend() =>
        new NerdctlEngine(new NerdctlCli(_runner, _namespace), Backend, DisplayName, _namespace);

    /// <summary>
    /// One provider per containerd namespace, read from <c>nerdctl namespace ls</c>.
    /// <para>
    /// Enumeration failing outright — nerdctl not installed, the CLI erroring, or printing nothing —
    /// still yields exactly one provider on <c>default</c> rather than none at all. Someone who
    /// installed this plugin needs to see that it is there; the switcher then shows "Not connected",
    /// the same way it already does for a Docker or Podman that is not running. Contributing nothing
    /// would look indistinguishable from the plugin failing to load.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NerdctlEngineProvider> DiscoverAll(IToolRunner runner)
    {
        // `namespace ls` is not scoped to any one namespace — the value here only satisfies
        // NerdctlCli's own rule that nothing calls the runner without going through it.
        var cli = new NerdctlCli(runner, "default");

        try
        {
            using var cts = new CancellationTokenSource(DiscoveryTimeout);
            var stdout = cli.RunAsync(cts.Token, "namespace", "ls", "--format", "json")
                .AsTask().GetAwaiter().GetResult();

            var names = NerdctlJson.Parse<NerdctlNamespace>(stdout)
                .Select(n => n.Name)
                .Where(name => name != DockerSharedNamespace)
                .ToList();

            if (names.Count == 0)
                return [new NerdctlEngineProvider("default", runner)];

            return [.. names.Select(name => new NerdctlEngineProvider(name, runner))];
        }
        catch (Exception)
        {
            return [new NerdctlEngineProvider("default", runner)];
        }
    }
}
