using System.Runtime.CompilerServices;
using System.Text.Json;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// CEAL implementation backed by the nerdctl CLI (KON-141) — one instance per containerd namespace,
/// matching the one-provider-per-namespace shape <see cref="NerdctlEngineProvider"/> already exposes.
/// <para>
/// As of this PR (nerdctl PR 4), every <see cref="IContainerEngine"/> member this CLI can actually
/// serve is implemented: reading (PR 2), writing (PR 3), and now live stats and events, image build,
/// Compose, and the image write side — pull, tag, remove. All of it against the CLI shapes captured in
/// Notes/nerdctl-cli-formats.md, Notes/nerdctl-write-formats.md and Notes/nerdctl-advanced-formats.md,
/// never against nerdctl's documentation.
/// </para>
/// <para>
/// What still throws <see cref="NotSupportedException"/> is not deferred work with a PR number on it —
/// each one names a limitation of the tool or the seam:
/// <list type="bullet">
/// <item><description><see cref="ConnectNetworkAsync"/>/<see cref="DisconnectNetworkAsync"/> — nerdctl
/// 2.3.5 has no such subcommand at all (<see cref="NetworkAttachUnsupported"/>).</description></item>
/// <item><description><see cref="ExecAsync"/>/<see cref="StartExecSessionAsync"/> — the tool seam can
/// read a process's output but cannot write to its stdin or give it a PTY
/// (<see cref="ExecUnsupported"/>).</description></item>
/// <item><description><see cref="VerifyRegistryLoginAsync"/>, <see cref="InspectImageAsync"/> and
/// <see cref="BrowseVolumeAsync"/> — nobody has captured what these commands print, and this plugin
/// does not map output it has not seen (<see cref="RegistryLoginUnobserved"/>,
/// <see cref="ImageInspectUnobserved"/>, <see cref="VolumeBrowseUnobserved"/>).</description></item>
/// <item><description><see cref="BuildImageAsync"/> — only while buildkitd is out of reach; see
/// <see cref="_buildkit"/>, which is the one capability here that varies per machine.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class NerdctlEngine : IContainerEngine
{
    private const string NetworkAttachUnsupported =
        "nerdctl 2.3.5 has no 'network connect' or 'network disconnect' subcommand — attaching or " +
        "detaching a container from a network is not possible through this backend, and no future " +
        "PR will change that; it would take nerdctl itself gaining the subcommand.";

    private const string ExecUnsupported =
        "Running a command inside a container is not available on this backend. nerdctl is driven " +
        "through the tool seam, which starts a process and reads its output but cannot write to its " +
        "stdin or attach a pseudo-terminal — so a terminal opened here would echo nothing, never " +
        "resize, and never deliver Ctrl-C. Closing this gap takes a PTY-capable seam, not another " +
        "nerdctl flag, so the capability says no rather than offering half a terminal.";

    private const string ImageInspectUnobserved =
        "What 'nerdctl image inspect' prints has not been captured against a real containerd, and this " +
        "plugin maps only output it has seen — mapping this one from nerdctl's documentation is exactly " +
        "the mistake its fixtures exist to prevent.";

    private const string RegistryLoginUnobserved =
        "What 'nerdctl login' prints against a real registry has not been captured " +
        "(Notes/nerdctl-advanced-formats.md names it as unobserved), so this backend cannot say whether " +
        "a credential is good — and guessing would report a working login for one that is not.";

    private const string VolumeBrowseUnobserved =
        "Browsing a volume needs a throwaway container mounting it, which means an image that may not " +
        "exist in this containerd namespace and a pull that the caller never asked for. Neither that " +
        "command nor its output has been captured, so this backend does not offer it.";

    /// <summary>
    /// The buildkit candidate sockets nerdctl itself named when it refused to build
    /// (Notes/nerdctl-advanced-formats.md) — <c>/run/buildkit-&lt;namespace&gt;/buildkitd.sock</c> first,
    /// then <c>/run/buildkit/buildkitd.sock</c>. Used by <see cref="DetectBuildkit"/>, and repeated to
    /// the user verbatim when a build is refused, since that message is what tells them what to start.
    /// </summary>
    private const string BuildkitSocketRoot = "/run/buildkit";

    /// <summary>
    /// How long <see cref="StreamStatsAsync"/> waits between samples. Two seconds because each sample is
    /// its own <c>nerdctl</c> process: faster would spend more time starting processes than measuring,
    /// and the detail view it feeds updates a number, not an animation.
    /// </summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    private readonly NerdctlCli _cli;
    private readonly string _backend;
    private readonly string _displayName;
    private readonly string _namespace;
    private readonly string _buildkitSocketRoot;

    /// <summary>
    /// Whether the last successful <c>info</c> read named <c>name=rootless</c> in its security options.
    /// Starts false — the honest answer before anything has been observed — and is refreshed every
    /// <see cref="GetInfoAsync"/> call, since <see cref="Capabilities"/> has no async path of its own to
    /// read <c>info</c> through.
    /// </summary>
    private bool _rootless;

    /// <summary>
    /// Whether a buildkitd socket was on disk the last time <c>info</c> was read. Starts false — the
    /// honest answer before anything has been looked at — and is refreshed by <see cref="DetectBuildkit"/>
    /// on every <see cref="GetInfoAsync"/>/<see cref="PingAsync"/>, the same way <see cref="_rootless"/>
    /// is, since <see cref="Capabilities"/> has no async path of its own.
    /// <para>
    /// This is the one capability that cannot be answered by looking at nerdctl: <c>build</c> exists as a
    /// subcommand whether or not it can work, so its presence says nothing (Notes/nerdctl-advanced-formats.md).
    /// </para>
    /// </summary>
    private bool _buildkit;

    /// <param name="buildkitSocketRoot">
    /// Where <see cref="DetectBuildkit"/> looks for buildkitd's socket. Overridable only so a test can
    /// point it at a directory it controls: the real value is a fixed path on the host
    /// (<see cref="BuildkitSocketRoot"/>), and a test asserting against that would pass or fail
    /// depending on whether the machine running it happens to have buildkit installed.
    /// </param>
    public NerdctlEngine(
        NerdctlCli cli, string backend, string displayName, string @namespace,
        string buildkitSocketRoot = BuildkitSocketRoot)
    {
        _cli = cli;
        _backend = backend;
        _displayName = displayName;
        _namespace = @namespace;
        _buildkitSocketRoot = buildkitSocketRoot;
    }

    public string Backend => _backend;

    /// <summary>
    /// Stats, events and Compose turn on in this PR alongside the prune flag PR 3 lit up: every method
    /// behind them runs a real nerdctl command now. The three that stay false stay false permanently as
    /// far as this plugin is concerned — <see cref="EngineCapabilities.SupportsExec"/> because the seam
    /// has no stdin or PTY (<see cref="ExecUnsupported"/>),
    /// <see cref="EngineCapabilities.SupportsVolumeBrowse"/> because that would take a throwaway
    /// container nobody has captured (<see cref="VolumeBrowseUnobserved"/>), and
    /// <see cref="EngineCapabilities.SupportsGpu"/> because no adapter implements GPU passthrough at all.
    /// <para>
    /// <see cref="EngineCapabilities.SupportsBuild"/> is the only flag here that differs from machine to
    /// machine: it follows <see cref="_buildkit"/>, since <c>nerdctl build</c> without a running
    /// buildkitd fails every time. <see cref="EngineCapabilities.Rootless"/> is likewise an observation
    /// read off <c>info</c>, not a promise about a method.
    /// </para>
    /// </summary>
    public EngineCapabilities Capabilities => new()
    {
        Rootless = _rootless,
        SupportsBuild = _buildkit,
        SupportsCompose = true,
        SupportsExec = false,

        // `nerdctl run --restart` takes the same policy names Docker's CLI does — see MapRestart.
        SupportsRestartPolicy = true,
        SupportsPrune = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = true,
        SupportsVolumeBrowse = false,
    };

    public ValueTask PingAsync(CancellationToken ct = default) =>
        new(ReadInfoAsync(ct).AsTask());

    public async ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var info = await ReadInfoAsync(ct).ConfigureAwait(false);
        return new BackendInfo
        {
            Backend = _backend,
            DisplayName = _displayName,
            Kind = "container engine",
            // `ServerVersion` in nerdctl's `info` is containerd's own version (e.g. "v2.3.1"), never
            // nerdctl's — nerdctl does not report its own version in this payload at all.
            Version = info.ServerVersion,
            // The namespace, not a socket path — that is what tells one nerdctl backend apart from
            // another in the switcher, where Docker would show its socket.
            Endpoint = _namespace,
            ConnectionState = EngineConnectionState.Connected,
        };
    }

    /// <summary>
    /// Runs <c>nerdctl --namespace &lt;ns&gt; info --format json</c> — the one call both
    /// <see cref="PingAsync"/> and <see cref="GetInfoAsync"/> need — and refreshes
    /// <see cref="_rootless"/> from it. A missing binary or a non-zero exit both mean the same thing to
    /// a caller: this backend cannot be reached, so both become <see cref="EngineUnreachableException"/>,
    /// the same exception the other adapters raise for a stopped engine, so
    /// <c>BackendRegistry.ProbeAsync</c> counts this backend as "Not connected" the same way.
    /// </summary>
    private async ValueTask<NerdctlInfo> ReadInfoAsync(CancellationToken ct)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, "info", "--format", "json").ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineUnreachableException($"nerdctl did not respond for '{_backend}': {ex.Message}", ex);
        }

        IReadOnlyList<NerdctlInfo> rows;
        try
        {
            rows = NerdctlJson.Parse<NerdctlInfo>(stdout);
        }
        catch (JsonException ex)
        {
            // Garbled `info` output means the same thing to a caller as the tool failures just above:
            // this backend cannot be understood right now, so PingAsync's callers see the one kind of
            // failure this whole method exists to guarantee, not a raw System.Text.Json type.
            throw new EngineUnreachableException($"nerdctl returned output for '{_backend}' that could not be parsed: {ex.Message}", ex);
        }

        if (rows.Count == 0)
            throw new EngineUnreachableException($"'nerdctl info' returned nothing for '{_backend}'.");
        var info = rows[0];

        // There is no `Rootless` field in `info` at all — the trap this plugin's fixtures were captured
        // specifically to catch. Rootless is signalled the same way Docker signals it: a
        // "name=rootless" entry in SecurityOptions, nothing else.
        _rootless = info.SecurityOptions.Contains("name=rootless", StringComparer.Ordinal);

        DetectBuildkit();

        return info;
    }

    /// <summary>
    /// Answers whether a build could work, by looking for buildkitd's socket on the same paths nerdctl
    /// named when it refused to build without one. Asking nerdctl itself is not an option: <c>build</c>
    /// exists as a subcommand regardless, and the only command that proves buildkitd is reachable is a
    /// build — which is not something to run behind a user's back to fill in a capability flag.
    /// <para>
    /// <c>BUILDKIT_HOST</c> is taken at its word: nerdctl consults it before any default path, and it may
    /// point at a TCP address this plugin cannot probe. Better to offer a build that then fails with
    /// nerdctl's own message than to hide the button from someone who configured buildkit deliberately.
    /// </para>
    /// <para>
    /// ponytail: a socket-file probe, not a handshake. A nerdctl reached through Lima or WSL keeps its
    /// sockets inside that VM, so this reports false there and the UI hides a build that would have
    /// worked — the safe direction to be wrong in. Upgrade to an actual buildkitd dial if that setup
    /// starts mattering.
    /// </para>
    /// </summary>
    private void DetectBuildkit()
    {
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("BUILDKIT_HOST")))
        {
            _buildkit = true;
            return;
        }

        _buildkit = BuildkitSockets().Any(File.Exists);
    }

    /// <summary>
    /// The socket paths nerdctl printed as its two candidates, in its own order — the namespaced one
    /// first — plus the rootless location, where a rootless nerdctl puts buildkitd instead.
    /// </summary>
    private IEnumerable<string> BuildkitSockets()
    {
        yield return $"{_buildkitSocketRoot}-{_namespace}/buildkitd.sock";
        yield return $"{_buildkitSocketRoot}/buildkitd.sock";

        if (System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDir)
            yield return Path.Combine(runtimeDir, "buildkit", "buildkitd.sock");
    }

    /// <summary>
    /// Runs a listing command (<c>ps</c>, <c>images</c>, <c>network ls</c>, <c>volume ls</c>), then
    /// hands stdout to <paramref name="parse"/> — the caller's own <c>NerdctlJson.Parse</c> + mapping —
    /// and translates every way either step can fail to the exceptions the rest of the CEAL already
    /// expects: the same translation <see cref="ReadInfoAsync"/> and <see cref="InspectContainerAsync"/>
    /// apply to their own commands, so a caller sees one consistent family of failures from this engine,
    /// never a raw tooling exception or a raw <see cref="JsonException"/>.
    /// <para>
    /// <c>NerdctlJson.Parse</c>/<c>ParseArray</c> already guarantee malformed JSON and a <c>null</c>
    /// entry both surface as <see cref="JsonException"/> rather than a
    /// <see cref="NullReferenceException"/> three lines later in the mapping below — that guard lives
    /// once in <c>NerdctlJson</c> so every caller inherits it. This still catches
    /// <see cref="NullReferenceException"/> alongside it as a defensive backstop for whatever the
    /// mapping delegate itself might do with a value <c>NerdctlJson</c> did hand back.
    /// </para>
    /// </summary>
    private async ValueTask<IReadOnlyList<T>> RunListAsync<T>(
        Func<string, IReadOnlyList<T>> parse, CancellationToken ct, params string[] args)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, args).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }

        try
        {
            return parse(stdout);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException)
        {
            throw new EngineException($"nerdctl returned output for '{_backend}' that could not be parsed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs a command whose output is plain text and returns its raw stdout, translating a missing binary
    /// or non-zero exit the same way <see cref="RunListAsync{T}"/> does. Unlike that method there is no
    /// JSON afterwards — the prune commands print the header-and-bare-lines shape
    /// Notes/nerdctl-write-formats.md captured, which each prune method counts itself with
    /// <see cref="CountAfterHeader"/> or <see cref="CountUntaggedLines"/>, while <c>tag</c> and
    /// <c>rmi</c> print something no caller here needs to read at all.
    /// <para>
    /// No not-found translation happens here, unlike <see cref="RunLifecycleAsync"/>: none of these
    /// commands has a failure marker captured against a real containerd, and inventing one would report
    /// a missing image for what might be a permission error.
    /// </para>
    /// </summary>
    private async ValueTask<string> RunTextAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            return await _cli.RunAsync(ct, args).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }
    }

    /// <summary>The failure every command here reports the same way: nerdctl ran, said no, and its own
    /// sentence is the most useful thing anyone can be told about why.</summary>
    private EngineException Failed(ToolFailedException ex) =>
        new($"nerdctl failed for '{_backend}': {ex.Message}", ex);

    /// <summary>
    /// Streams a command's output line by line, translating the two tooling failures into the CEAL's own
    /// exceptions — a missing binary always to <see cref="EngineUnreachableException"/>, a non-zero exit
    /// to whatever <paramref name="onFailure"/> decides, since "no such container" means one thing for
    /// <c>logs</c> and another for <c>events</c>.
    /// <para>
    /// The enumerator is driven by hand rather than with <c>await foreach</c> because C# forbids
    /// <c>yield return</c> inside a <c>try</c> that has a <c>catch</c> — and translating those exceptions
    /// is the entire point of this method. Every streaming caller shares it so the translation is written
    /// once instead of once per command.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<ToolLine> StreamOrThrowAsync(
        Func<ToolFailedException, Exception> onFailure,
        [EnumeratorCancellation] CancellationToken ct,
        params string[] args)
    {
        var lines = _cli.StreamAsync(ct, args).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool has;
                try
                {
                    has = await lines.MoveNextAsync().ConfigureAwait(false);
                }
                catch (ToolNotFoundException ex)
                {
                    throw new EngineUnreachableException(
                        $"nerdctl is not installed — cannot reach '{_backend}'.", ex);
                }
                catch (ToolFailedException ex)
                {
                    throw onFailure(ex);
                }

                if (!has)
                    yield break;

                yield return lines.Current;
            }
        }
        finally
        {
            await lines.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The same stream as <see cref="StreamOrThrowAsync"/>, but for the two commands a user watches —
    /// <c>build</c> and <c>compose</c> — where a failure is the last line of the log rather than an
    /// exception. <see cref="BuildProgress"/> and <see cref="ComposeProgress"/> both carry an optional
    /// error string for exactly this, and the view models show it: a build that throws halfway would
    /// throw away the output explaining why it failed.
    /// <para>
    /// nerdctl's own message is what lands in that error, unchanged. Both commands narrate on stderr —
    /// BuildKit's step output and Compose's logrus lines are both there, not on stdout — so the streams
    /// are merged here, exactly as <see cref="ToolRunner"/> already merges them, and the caller decides
    /// what to make of each line.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<(string Text, string? Error)> StreamProgressAsync(
        [EnumeratorCancellation] CancellationToken ct, params string[] args)
    {
        var lines = StreamOrThrowAsync(Failed, ct, args).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool has;
                string? failure = null;
                try
                {
                    has = await lines.MoveNextAsync().ConfigureAwait(false);
                }
                catch (EngineException ex)
                {
                    // Both failures StreamOrThrowAsync raises land here — it is the only place that
                    // knows about the tooling exceptions, and EngineUnreachableException derives from
                    // EngineException, so neither needs naming twice.
                    has = false;
                    failure = ex.Message;
                }

                if (failure is not null)
                {
                    yield return (failure, failure);
                    yield break;
                }

                if (!has)
                    yield break;

                yield return (lines.Current.Text, null);
            }
        }
        finally
        {
            await lines.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Counts the bare name/id lines <c>container prune</c> and <c>volume prune</c> both print under
    /// their header (Notes/nerdctl-write-formats.md) — every line after <paramref name="header"/> is one
    /// removed item, and nothing to prune means the header itself never appears, which this reports as
    /// zero rather than searching for a header that will not be found.
    /// </summary>
    private static int CountAfterHeader(string stdout, string header)
    {
        var lines = NerdctlJson.Lines(stdout).ToList();
        return lines.Contains(header) ? lines.Count - 1 : 0;
    }

    /// <summary>
    /// Counts removed images from <c>image prune</c>'s output: one <c>Untagged: &lt;ref&gt;</c> line per
    /// image, each optionally followed by <c>deleted: sha256:…</c> lines for its layers
    /// (Notes/nerdctl-write-formats.md). Counting every line instead would count one image's layers as
    /// separate removals; nothing to prune prints no <c>Untagged:</c> lines at all, so this needs no
    /// separate empty-output case.
    /// </summary>
    private static int CountUntaggedLines(string stdout) =>
        NerdctlJson.Lines(stdout).Count(line => line.StartsWith("Untagged: ", StringComparison.Ordinal));

    // ── Containers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>ps --format json</c>, adding <c>-a</c> only when <paramref name="all"/> is true. Getting
    /// this backwards is silent either way: without <c>-a</c>, a stopped container simply is not in the
    /// output — nerdctl does not say it hid anything — and with an unwanted <c>-a</c>, stopped
    /// containers appear as if they were live inventory.
    /// </summary>
    public async ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default)
    {
        string[] args = all ? ["ps", "-a", "--format", "json"] : ["ps", "--format", "json"];

        return await RunListAsync<ContainerSummary>(
            stdout => [.. NerdctlJson.Parse<NerdctlContainer>(stdout).Select(c => c.ToSummary(_backend))],
            ct,
            args).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>nerdctl create</c>, then <c>start</c> when <see cref="CreateContainerRequest.Start"/> asks
    /// for it — two calls, not one, because <c>create</c> alone never starts the container (the same
    /// two-step shape <c>DockerEngine</c> uses). Every field on <see cref="CreateContainerRequest"/> maps
    /// onto a real flag (see <see cref="BuildCreateArgs"/>); <c>Start</c> is the only one that is not a
    /// flag at all, which is why it drives this second call instead.
    /// <para>
    /// Unlike the lifecycle commands, <c>create</c>'s stdout <i>is</i> the answer: it prints the full
    /// 64-character id (Notes/nerdctl-write-formats.md), so this reads and trims it rather than echoing
    /// back something the caller already had. <c>ps</c> later reports only the 12-character short id, so
    /// the value returned here will not literally equal what <see cref="ListContainersAsync"/> shows for
    /// the same container afterwards — a caller correlating the two must compare by prefix, not equality.
    /// </para>
    /// <para>
    /// <b>If <c>create</c> succeeds but the follow-up <c>start</c> throws, this method never returns</b>:
    /// the caller gets an exception, not the id, even though the container now exists on the backend in
    /// a stopped state. It is not orphaned — <see cref="ListContainersAsync"/> or a retried
    /// <see cref="StartContainerAsync"/> against the name/id still reaches it, and nerdctl's own failure
    /// text (surfaced unchanged by <see cref="RunLifecycleAsync"/>) usually names the container — but the
    /// id is not handed back structurally. This is a deliberate gap, not an oversight: it matches
    /// <c>DockerEngine.CreateContainerAsync</c>, which has exactly the same shape against the raw Docker
    /// API, so a caller already has to handle this for the other adapter too.
    /// </para>
    /// </summary>
    public async ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, BuildCreateArgs(request)).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            // Unlike RunLifecycleAsync's failures, there is no id yet for nerdctl to complain about not
            // finding — a name already in use, a missing image, or an unparseable flag all exit non-zero
            // here undistinguished, since none of them were observed with a marker as stable as "no such
            // container" (Notes/nerdctl-write-formats.md only documents the lifecycle and
            // volume-in-use cases). nerdctl's own sentence is kept whole rather than replaced with
            // something generic, the same choice RunLifecycleAsync makes for its own fallback case.
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }

        // The doc comment above notes nerdctl auto-pulls a missing image, and today that pull progress
        // goes to stderr, not stdout — but if it ever landed on stdout too, a bare `.Trim()` of the
        // whole blob would hand the id-plus-progress mush to `start` below and back to the caller. The
        // last non-empty line is the id in both the observed single-line case and that hypothetical
        // one, at no extra cost. Empty stdout is not "no id" silently swallowed: it means create
        // reported success without printing anything to identify what it made, which is a failure this
        // engine should say so about rather than hand back null as if that were a normal id.
        var id = NerdctlJson.Lines(stdout).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            throw new EngineException(
                $"nerdctl reported a container was created on '{_backend}' but printed no id.");
        }

        if (request.Start)
            await RunLifecycleAsync("Container", id, "no such container", ct, "start", id).ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// Translates <see cref="CreateContainerRequest"/> into a <c>create</c> command line. Every field
    /// maps onto a real nerdctl flag — name, ports, environment, volumes, network and restart policy are
    /// all part of nerdctl's Docker-compatible <c>create</c>/<c>run</c> flag set — so nothing on the
    /// request is dropped silently; <see cref="CreateContainerRequest.Start"/> is handled by the caller
    /// as a separate <c>start</c> call instead, since it has no flag of its own.
    /// <para>
    /// No explicit "does the image exist locally" check runs before this, unlike <c>DockerEngine</c>:
    /// that adapter talks to the raw Engine API, which does not pull on create, while this shells out to
    /// the actual <c>nerdctl</c> CLI, which auto-pulls a missing image itself — the same reason
    /// <c>docker create</c> does not need a caller to pull first either.
    /// </para>
    /// </summary>
    private static string[] BuildCreateArgs(CreateContainerRequest request)
    {
        List<string> args = ["create"];

        if (request.Name is { } name)
            args.AddRange(["--name", name]);

        foreach (var port in request.Ports)
        {
            // A null HostPort means "publish to a random host port", not "don't publish" — every entry
            // in Ports is meant to be published, matching how DockerEngine maps this same request field.
            var spec = port.HostPort is { } hostPort
                ? $"{hostPort}:{port.ContainerPort}/{port.Protocol}"
                : $"{port.ContainerPort}/{port.Protocol}";
            args.AddRange(["-p", spec]);
        }

        foreach (var (key, value) in request.Environment)
            args.AddRange(["-e", $"{key}={value}"]);

        foreach (var mount in request.Mounts)
            args.AddRange(["-v", mount.ReadOnly
                ? $"{mount.Source}:{mount.Target}:ro"
                : $"{mount.Source}:{mount.Target}"]);

        if (request.Network is { } network)
            args.AddRange(["--network", network]);

        if (request.RestartPolicy != RestartPolicy.No)
            args.AddRange(["--restart", MapRestart(request.RestartPolicy)]);

        args.Add(request.Image);

        return [.. args];
    }

    /// <summary>
    /// nerdctl's <c>--restart</c> accepts the same policy names Docker's CLI does. <see cref="RestartPolicy.No"/>
    /// never reaches here — <see cref="BuildCreateArgs"/> skips the flag entirely for it, since that is
    /// nerdctl's own default and spelling it out on every command line would just be noise.
    /// </summary>
    private static string MapRestart(RestartPolicy policy) => policy switch
    {
        RestartPolicy.Always => "always",
        RestartPolicy.OnFailure => "on-failure",
        RestartPolicy.UnlessStopped => "unless-stopped",
        _ => "no",
    };

    /// <summary>Runs <c>nerdctl start &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart.</summary>
    public ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Container", id, "no such container", ct, "start", id);

    /// <summary>Runs <c>nerdctl stop &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart.</summary>
    public ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Container", id, "no such container", ct, "stop", id);

    /// <summary>Runs <c>nerdctl restart &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart.</summary>
    public ValueTask RestartContainerAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Container", id, "no such container", ct, "restart", id);

    /// <summary>Runs <c>nerdctl pause &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart.</summary>
    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Container", id, "no such container", ct, "pause", id);

    /// <summary>Runs <c>nerdctl unpause &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart.</summary>
    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Container", id, "no such container", ct, "unpause", id);

    /// <summary>
    /// Runs <c>nerdctl rm &lt;id&gt;</c>, adding <c>-f</c> only when <paramref name="force"/> is true.
    /// That flag is the difference between succeeding and hitting the "is in running status" conflict
    /// <see cref="RunLifecycleAsync"/> documents — nerdctl refuses to remove a running container
    /// without it.
    /// </summary>
    public ValueTask RemoveContainerAsync(string id, bool force = false, CancellationToken ct = default) =>
        force
            ? RunLifecycleAsync("Container", id, "no such container", ct, "rm", "-f", id)
            : RunLifecycleAsync("Container", id, "no such container", ct, "rm", id);

    /// <summary>
    /// Runs a bare lifecycle or removal command (<c>start</c>, <c>stop</c>, <c>restart</c>,
    /// <c>pause</c>, <c>unpause</c>, <c>rm</c>, <c>volume rm</c>, <c>network rm</c>) whose only
    /// meaningful result is the exit code: every one of these echoes back the very name or id it was
    /// given, never a new identifier, so unlike <see cref="RunListAsync{T}"/> there is nothing on
    /// stdout worth reading on success. Shared across containers, volumes and networks rather than
    /// copied per resource kind — the failure shapes below are the same regardless of which resource
    /// nerdctl was told to act on.
    /// <para>
    /// Two <see cref="ToolFailedException"/> shapes are told apart here rather than folded into one
    /// exception. <paramref name="notFoundMarker"/> (e.g. <c>"no such container"</c>) means the id
    /// nerdctl was given does not exist — a <see cref="ResourceNotFoundException"/>, the same exception
    /// <see cref="InspectContainerAsync"/> already raises for the container case. Every other failure —
    /// <c>rm</c> on a running container ("is in running status…") or <c>volume rm</c> on one still
    /// mounted ("some volumes could not be removed") — is a conflict over the resource's current state,
    /// not a missing resource: the id is perfectly real, nerdctl just refuses to act on it right now.
    /// Reporting that as "not found" would be a wrong answer stated as fact, so it surfaces as the base
    /// <see cref="EngineException"/> instead, with nerdctl's own sentence kept whole — it already names
    /// the resource, the state, and (for containers) the two ways out, which is more than a generic
    /// message could tell the caller.
    /// </para>
    /// </summary>
    private async ValueTask RunLifecycleAsync(
        string resourceKind, string id, string notFoundMarker, CancellationToken ct, params string[] args)
    {
        try
        {
            await _cli.RunAsync(ct, args).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex) when (ex.Message.Contains(notFoundMarker, StringComparison.Ordinal))
        {
            throw new ResourceNotFoundException($"{resourceKind} '{id}' was not found on '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs <c>nerdctl inspect &lt;id&gt;</c> — Docker-compatible, a JSON array with one element (see
    /// <see cref="NerdctlJson.ParseArray{T}"/>). An id nerdctl does not know about makes the CLI exit
    /// non-zero, surfaced by <see cref="NerdctlCli"/> as <see cref="ToolFailedException"/>; that is a
    /// tooling-layer exception naming a raw command line, not something the CEAL boundary should let
    /// through, so it becomes the same <see cref="ResourceNotFoundException"/> every other adapter
    /// raises for an unknown id.
    /// </summary>
    public async ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        string stdout;
        try
        {
            stdout = await _cli.RunAsync(ct, "inspect", id).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.", ex);
        }

        IReadOnlyList<NerdctlInspectContainer> rows;
        try
        {
            rows = NerdctlJson.ParseArray<NerdctlInspectContainer>(stdout);
        }
        catch (JsonException ex)
        {
            // Garbled `inspect` output is not "id not found" (ResourceNotFoundException would be a
            // wrong answer stated as fact) and not "cannot reach the backend" (nerdctl did respond) —
            // it is nerdctl printing something this plugin cannot read, the same generic failure
            // RunListAsync already reports for the same reason on its own commands.
            throw new EngineException($"nerdctl returned output for '{_backend}' that could not be parsed: {ex.Message}", ex);
        }

        if (rows.Count == 0)
            throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.");

        return rows[0].ToInspect();
    }

    /// <summary>
    /// Not available here — see <see cref="ExecUnsupported"/>. <c>nerdctl exec</c> itself would work for
    /// this one-shot case (its exit code is readable, if only out of the
    /// <c>"exec failed with exit code &lt;n&gt;"</c> line it prints — nerdctl does not hand it back any
    /// other way, Notes/nerdctl-advanced-formats.md). It is left unimplemented anyway because
    /// <see cref="EngineCapabilities.SupportsExec"/> guards this method and
    /// <see cref="StartExecSessionAsync"/> together, and that one genuinely cannot be built on this seam:
    /// a flag that says yes would put a terminal in the UI that cannot type.
    /// </summary>
    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(ExecUnsupported);

    /// <summary>
    /// Not available here — see <see cref="ExecUnsupported"/>. <see cref="IToolRunner"/> exposes
    /// <c>RunAsync</c> and <c>StreamAsync</c>: a process is started and its output read, with no way to
    /// write to its stdin and no pseudo-terminal, which is exactly what <see cref="IExecSession"/>'s
    /// <c>WriteAsync</c> and <c>ResizeAsync</c> require.
    /// </summary>
    public ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(ExecUnsupported);

    /// <summary>
    /// Runs <c>nerdctl container prune -f</c> (<c>-f</c> skips the confirmation prompt nerdctl otherwise
    /// blocks on, which a non-interactive caller would never answer). A real capture against nerdctl
    /// 2.3.5 shows nothing at all on stdout — not even the <c>Deleted Containers:</c> header — when
    /// there were no stopped containers to remove (Notes/nerdctl-write-formats.md); <see cref="CountAfterHeader"/>
    /// already treats a header that never appears as zero, so that ordinary case reaches here as an
    /// empty result rather than needing a special case.
    /// </summary>
    public async ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default)
    {
        var stdout = await RunTextAsync(ct, "container", "prune", "-f").ConfigureAwait(false);
        return new PruneResult(CountAfterHeader(stdout, "Deleted Containers:"), 0);
    }

    // ── Images ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default) =>
        await RunListAsync<ImageSummary>(
            stdout => [.. NerdctlJson.Parse<NerdctlImage>(stdout).Select(i => i.ToImage())],
            ct,
            "images", "--format", "json").ConfigureAwait(false);

    /// <summary>
    /// Streams <c>nerdctl pull &lt;reference&gt;</c>. nerdctl narrates a pull in free-form progress lines
    /// with no per-layer byte counts a caller could total up (Notes/nerdctl-advanced-formats.md), so
    /// <see cref="PullProgress.Current"/> and <see cref="PullProgress.Total"/> stay <c>null</c> rather
    /// than carrying a number derived from the text — the UI shows an indeterminate pull, which is the
    /// truth here, instead of a progress bar built on a guess.
    /// <para>
    /// A <paramref name="credential"/> is refused rather than ignored. Authenticating means
    /// <c>nerdctl login</c> first, and that command is unobserved (<see cref="RegistryLoginUnobserved"/>);
    /// pulling anyway would work for public images and fail confusingly for the private one the caller
    /// supplied a credential for in the first place.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (credential is not null)
            throw new NotSupportedException(RegistryLoginUnobserved);

        await foreach (var line in StreamOrThrowAsync(Failed, ct, "pull", reference).ConfigureAwait(false))
            yield return new PullProgress(reference, line.Text, null, null);
    }

    /// <summary>Not available here — see <see cref="RegistryLoginUnobserved"/>.</summary>
    public ValueTask VerifyRegistryLoginAsync(RegistryCredential credential, CancellationToken ct = default) =>
        throw new NotSupportedException(RegistryLoginUnobserved);

    /// <summary>
    /// Streams <c>nerdctl build --progress=plain</c>. That flag is not cosmetic: nerdctl's default
    /// progress output redraws itself the way a TTY progress bar does, which read line by line is a
    /// stream of near-duplicate fragments rather than a build log (Notes/nerdctl-advanced-formats.md).
    /// <para>
    /// Refused outright when no buildkitd was found (<see cref="DetectBuildkit"/>), naming both socket
    /// paths nerdctl itself named and the URL it points at. Letting the build run instead would produce
    /// the same failure a few seconds later, with the reason buried under the build's own noise — and
    /// <see cref="EngineCapabilities.SupportsBuild"/> already told the UI not to offer this.
    /// </para>
    /// <para>
    /// The Dockerfile is resolved against the context directory rather than passed through as-is: nerdctl
    /// reads <c>-f</c> relative to the working directory, which is Kontena's, not the caller's context.
    /// A <see cref="BuildRequest.Dockerfile"/> that is already absolute is left alone.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_buildkit)
        {
            throw new NotSupportedException(
                "'nerdctl build' needs a running buildkitd, and none was found for " +
                $"'{_backend}' — tried {string.Join(" and ", BuildkitSockets())}. " +
                "See https://github.com/moby/buildkit for how to start one.");
        }

        if (!Directory.Exists(request.ContextPath))
        {
            yield return new BuildProgress(
                $"Build context not found: {request.ContextPath}",
                $"Build context not found: {request.ContextPath}");
            yield break;
        }

        var dockerfile = string.IsNullOrWhiteSpace(request.Dockerfile) ? "Dockerfile" : request.Dockerfile;

        List<string> args =
        [
            "build",
            "--progress=plain",
            "-f", Path.IsPathRooted(dockerfile) ? dockerfile : Path.Combine(request.ContextPath, dockerfile),
            "-t", request.Tag,
        ];

        if (!string.IsNullOrWhiteSpace(request.Target))
            args.AddRange(["--target", request.Target]);
        if (request.NoCache)
            args.Add("--no-cache");
        if (request.Pull)
            args.Add("--pull");

        foreach (var (key, value) in request.BuildArgs)
            args.AddRange(["--build-arg", $"{key}={value}"]);

        args.Add(request.ContextPath);

        await foreach (var line in StreamProgressAsync(ct, [.. args]).ConfigureAwait(false))
            yield return new BuildProgress(line.Text, line.Error);
    }

    /// <summary>
    /// Runs <c>nerdctl rmi &lt;id&gt;</c>, adding <c>-f</c> only when <paramref name="force"/> is true.
    /// Its output — one <c>Deleted: sha256:…</c> line per layer — is not read: this method returns
    /// nothing, and the count only matters for <see cref="PruneImagesAsync"/>, which parses its own
    /// (lowercase <c>deleted:</c>, a different word for the same thing).
    /// </summary>
    public async ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default)
    {
        string[] args = force ? ["rmi", "-f", id] : ["rmi", id];
        await RunTextAsync(ct, args).ConfigureAwait(false);
    }

    /// <summary>Not available here — see <see cref="ImageInspectUnobserved"/>.</summary>
    public ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default) =>
        throw new NotSupportedException(ImageInspectUnobserved);

    /// <summary>
    /// Runs <c>nerdctl tag &lt;id&gt; &lt;newTag&gt;</c>, which prints nothing at all on success and exits
    /// zero (Notes/nerdctl-advanced-formats.md) — so, as with the lifecycle commands, the exit code is
    /// the entire result and there is nothing on stdout worth reading.
    /// </summary>
    public async ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default) =>
        await RunTextAsync(ct, "tag", id, newTag).ConfigureAwait(false);

    /// <summary>
    /// Runs <c>nerdctl image prune -f</c>, adding <c>--all</c> when <paramref name="allUnused"/> asks for
    /// every unused image rather than only dangling ones — the same distinction
    /// <c>DockerEngine.PruneImagesAsync</c> makes for the Docker Engine API's own <c>dangling=false</c>
    /// filter. Unlike the bare name/id lines <see cref="PruneContainersAsync"/> and
    /// <see cref="PruneVolumesAsync"/> parse, each removed image prints an <c>Untagged: &lt;ref&gt;</c>
    /// line followed by zero or more <c>deleted: sha256:…</c> lines for its layers
    /// (Notes/nerdctl-write-formats.md) — so the count is the number of <c>Untagged:</c> lines, not the
    /// total line count: counting every line would count one image's layers as separate removals.
    /// </summary>
    public async ValueTask<PruneResult> PruneImagesAsync(bool allUnused = true, CancellationToken ct = default)
    {
        List<string> args = ["image", "prune", "-f"];
        if (allUnused)
            args.Add("--all");

        var stdout = await RunTextAsync(ct, [.. args]).ConfigureAwait(false);
        return new PruneResult(CountUntaggedLines(stdout), 0);
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>volume ls --format json</c>. On a machine with no volumes — the ordinary state — nerdctl
    /// prints nothing at all: not <c>[]</c>, not a blank line. <see cref="NerdctlJson.Lines"/> already
    /// treats zero bytes as zero rows, so that ordinary case reaches here as an empty list rather than
    /// as something this method needs to special-case or that could throw.
    /// </summary>
    public async ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default) =>
        await RunListAsync<VolumeSummary>(
            stdout => [.. NerdctlJson.Parse<NerdctlVolume>(stdout).Select(v => v.ToVolume())],
            ct,
            "volume", "ls", "--format", "json").ConfigureAwait(false);

    /// <summary>
    /// Runs <c>nerdctl volume create</c>, then reads the created volume back with
    /// <see cref="ListVolumesAsync"/> instead of constructing a <see cref="VolumeSummary"/> from the
    /// request. <c>volume create</c> only echoes the name it was given (Notes/nerdctl-write-formats.md)
    /// — no driver, no mountpoint — so building the summary from <paramref name="request"/> would report
    /// <see cref="VolumeSummary.Driver"/> and <see cref="VolumeSummary.Mountpoint"/> as whatever was
    /// asked for rather than what nerdctl actually created. Reading it back is what proves this.
    /// <para>
    /// <see cref="CreateVolumeRequest.Driver"/> is checked against <c>"local"</c> — the request record's
    /// own default, meaning "no preference" — before anything runs. <c>volume create --help</c> lists
    /// only <c>--label</c>; nerdctl has exactly one volume driver and no flag to select another, so a
    /// request naming a different one cannot be honoured. This is a write-side input the caller chose,
    /// not a read-side field nerdctl simply never reports (contrast <see cref="NerdctlMap.ToNetwork"/>'s
    /// <c>Driver</c>, which is genuinely absent from nerdctl's own output): silently substituting
    /// <c>"local"</c> here would report success while doing something other than what was asked, with no
    /// signal anywhere that it happened — <c>CreateVolumeViewModel</c> discards the returned
    /// <see cref="VolumeSummary"/>, so even a `Driver` field that came back wrong would never surface.
    /// Refusing before <c>volume create</c> ever runs is therefore the only honest option, and doing it
    /// before the call (rather than after) means a rejected request never creates the volume anyway.
    /// </para>
    /// </summary>
    public async ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default)
    {
        if (request.Driver != "local")
        {
            throw new EngineException(
                $"nerdctl supports only its built-in volume driver and has no flag to select " +
                $"'{request.Driver}'.");
        }

        List<string> args = ["volume", "create"];

        foreach (var (key, value) in request.Labels)
            args.AddRange(["--label", $"{key}={value}"]);

        args.Add(request.Name);

        try
        {
            await _cli.RunAsync(ct, [.. args]).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }

        var created = (await ListVolumesAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(v => v.Name == request.Name);

        if (created is null)
        {
            throw new EngineException(
                $"nerdctl reported volume '{request.Name}' created but it is not in 'volume ls' for '{_backend}'.");
        }

        return created;
    }

    /// <summary>
    /// Runs <c>nerdctl volume rm &lt;name&gt;</c>, adding <c>-f</c> only when <paramref name="force"/>
    /// is true — mirrors <see cref="RemoveContainerAsync"/>'s own flag.
    /// <para>
    /// <b>The fatal line alone cannot tell a missing volume apart from a busy one.</b> A real capture
    /// against nerdctl 2.3.5 shows both cases end in the identical fatal line
    /// <c>"some volumes could not be removed"</c> — a missing volume warns
    /// <c>volume "x": not found</c> first, a mounted one warns
    /// <c>volume "x" is in use (failed precondition)</c> first, and only that warning line
    /// distinguishes them. <see cref="RunLifecycleAsync"/> is therefore given <c>": not found"</c> — a
    /// fragment of the warning, not the shared fatal line — as its marker; matching on the fatal line
    /// instead (as an earlier version of this method did, before this was captured) would report every
    /// in-use volume as not-found, which is a wrong answer stated as fact. The in-use warning names no
    /// <c>": not found"</c> substring, so it still falls through to the generic conflict case, the same
    /// treatment <see cref="RunLifecycleAsync"/> already gives "is in running status" for containers.
    /// </para>
    /// </summary>
    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        force
            ? RunLifecycleAsync("Volume", name, "\": not found", ct, "volume", "rm", "-f", name)
            : RunLifecycleAsync("Volume", name, "\": not found", ct, "volume", "rm", name);

    /// <summary>Not available here — see <see cref="VolumeBrowseUnobserved"/>.</summary>
    public ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default) =>
        throw new NotSupportedException(VolumeBrowseUnobserved);

    /// <summary>
    /// Runs <c>nerdctl volume prune -f --all</c>. <c>--all</c> is not optional here the way it is on
    /// <see cref="PruneImagesAsync"/>: nerdctl's own default (no <c>--all</c>) removes only anonymous
    /// volumes, leaving named-but-unused ones behind, while <see cref="IContainerEngine.PruneVolumesAsync"/>
    /// promises "remove all volumes not used by any container" — the same behaviour
    /// <c>DockerEngine.PruneVolumesAsync</c> gets from Docker's own default (Docker draws no
    /// anonymous/named distinction for volume prune the way nerdctl does). Without <c>--all</c> this
    /// method would silently under-deliver on its own contract.
    /// </summary>
    public async ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default)
    {
        var stdout = await RunTextAsync(ct, "volume", "prune", "-f", "--all").ConfigureAwait(false);
        return new PruneResult(CountAfterHeader(stdout, "Deleted Volumes:"), 0);
    }

    // ── Networks ────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default) =>
        await RunListAsync<NetworkSummary>(
            stdout => [.. NerdctlJson.Parse<NerdctlNetwork>(stdout).Select(n => n.ToNetwork())],
            ct,
            "network", "ls", "--format", "json").ConfigureAwait(false);

    /// <summary>
    /// Runs <c>nerdctl network create</c>, then reads the created network back with
    /// <see cref="ListNetworksAsync"/> rather than trusting stdout for anything beyond existence.
    /// <c>network create</c> prints the full 64-character id (Notes/nerdctl-write-formats.md) — a
    /// different shape than <c>volume create</c>'s name, and still no driver or subnet — so the same
    /// "don't construct it from the request" reasoning <see cref="CreateVolumeAsync"/> documents applies
    /// here too. Looked up by <see cref="CreateNetworkRequest.Name"/> rather than the returned id: `ls`
    /// reports only a short id, and reserved/CNI-managed networks report none at all, so name is the
    /// one key guaranteed to match what was just created.
    /// </summary>
    public async ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default)
    {
        List<string> args = ["network", "create"];

        // "bridge" is nerdctl's own default driver — same reasoning as CreateVolumeAsync's "local".
        if (request.Driver != "bridge")
            args.AddRange(["--driver", request.Driver]);

        if (!string.IsNullOrWhiteSpace(request.Subnet))
            args.AddRange(["--subnet", request.Subnet]);

        args.Add(request.Name);

        try
        {
            await _cli.RunAsync(ct, [.. args]).ConfigureAwait(false);
        }
        catch (ToolNotFoundException ex)
        {
            throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
        }
        catch (ToolFailedException ex)
        {
            throw new EngineException($"nerdctl failed for '{_backend}': {ex.Message}", ex);
        }

        var created = (await ListNetworksAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(n => n.Name == request.Name);

        if (created is null)
        {
            throw new EngineException(
                $"nerdctl reported network '{request.Name}' created but it is not in 'network ls' for '{_backend}'.");
        }

        return created;
    }

    /// <summary>
    /// Runs <c>nerdctl network rm &lt;id&gt;</c> — see <see cref="RunLifecycleAsync"/> for how its
    /// failures are told apart. Unlike <see cref="RemoveVolumeAsync"/>'s ambiguous fatal line, a real
    /// capture against nerdctl 2.3.5 shows the not-found case here is unambiguous on its own: an unknown
    /// id or name answers <c>"no network found matching: &lt;id&gt;"</c>, a marker that appears in no
    /// other observed network-removal failure, so no warning-line workaround is needed the way
    /// <see cref="RemoveVolumeAsync"/> needs one. No force flag exists here:
    /// <see cref="IContainerEngine.RemoveNetworkAsync"/> does not take one, and nothing observed named a
    /// conflict case for network removal the way <see cref="RemoveVolumeAsync"/> has one.
    /// </summary>
    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        RunLifecycleAsync("Network", id, "no network found matching:", ct, "network", "rm", id);

    /// <summary>
    /// nerdctl 2.3.5 has no <c>network connect</c> subcommand at all — the CLI answers "unknown
    /// subcommand", not a failure this plugin could translate into success later. This is a permanent
    /// limitation of the tool this engine shells out to, not unfinished work, and unlike the other
    /// still-throwing members above it has no <see cref="EngineCapabilities"/> flag guarding it: nothing
    /// on that record names this method, so the UI has no capability to check before offering the action
    /// and must instead learn about the gap from here.
    /// </summary>
    public ValueTask ConnectNetworkAsync(
        string containerId, string networkId, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    /// <summary>
    /// Same limitation as <see cref="ConnectNetworkAsync"/>: nerdctl 2.3.5 has no <c>network disconnect</c>
    /// subcommand either, so this can never work against this CLI, and no <see cref="EngineCapabilities"/>
    /// flag exists to let the UI know ahead of calling it.
    /// </summary>
    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    // ── Compose ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>nerdctl compose up -d</c> and streams what it says. nerdctl's Compose narrates through
    /// logrus on stderr — <c>level=info msg="Creating container cmp-web-1"</c>
    /// (Notes/nerdctl-advanced-formats.md) — so the useful sentence is unwrapped with
    /// <see cref="NerdctlJson.Logrus"/> before it reaches the caller; passing the raw line through would
    /// put nerdctl's log plumbing in Kontena's Compose panel.
    /// <para>
    /// A missing compose file is answered here rather than by nerdctl, matching
    /// <c>DockerEngine.ComposeUpAsync</c>: the caller gets one clear line naming the path instead of the
    /// CLI's own error about a file it could not open.
    /// </para>
    /// <para>
    /// <c>level=error</c> and <c>level=fatal</c> lines are marked as failures as they arrive. Waiting for
    /// the non-zero exit would work too, but the process can keep talking for a while after the sentence
    /// that explains what went wrong, and the UI stops at the first error line it is given.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ComposeFilePath) || !File.Exists(request.ComposeFilePath))
        {
            var missing = $"Compose file not found: {request.ComposeFilePath}";
            yield return new ComposeProgress(missing, missing);
            yield break;
        }

        List<string> args = ["compose", "-f", request.ComposeFilePath];

        if (request.ProjectName is { Length: > 0 } project)
            args.AddRange(["-p", project]);

        args.AddRange(["up", "-d"]);

        if (request.Build)
            args.Add("--build");
        if (request.ForceRecreate)
            args.Add("--force-recreate");

        await foreach (var line in StreamProgressAsync(ct, [.. args]).ConfigureAwait(false))
        {
            if (line.Error is { } error)
            {
                yield return new ComposeProgress(error, error);
                yield break;
            }

            var (level, message) = NerdctlJson.Logrus(line.Text);
            var failed = level is "error" or "fatal";
            yield return new ComposeProgress(message, failed ? message : null);
        }
    }

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams <c>nerdctl logs &lt;id&gt;</c>, bare lines with no wrapper. <paramref name="follow"/>
    /// decides whether <c>-f</c> goes on the command line at all — without it, a "follow" reader would
    /// hang forever on a container that finished producing output.
    /// <para>
    /// Always asks for <c>--timestamps</c> rather than leaving <see cref="LogEntry.Timestamp"/> at
    /// <c>default</c>: nerdctl only stamps a line when asked, and <see cref="LogLine.Parse"/> already
    /// exists to read that stamp back off the line (the same helper <c>DockerEngine</c> uses), so asking
    /// for it costs nothing extra here. The alternative — inventing a timestamp from
    /// <see cref="DateTimeOffset.UtcNow"/> at read time — is exactly the mistake KON-203 fixed for
    /// Docker: a backlog of old lines all read in one burst would each get today's clock reading, which
    /// is a wrong answer presented as fact, not a missing one. <c>UtcNow</c> is passed to
    /// <see cref="LogLine.Parse"/> only as its fallback for the rare line nerdctl did not actually stamp.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> args = ["logs", "--timestamps"];
        if (follow)
            args.Add("-f");
        args.Add(id);

        // Same reasoning as InspectContainerAsync: an id nerdctl does not know about is by far the most
        // common reason `logs` exits non-zero, so it becomes the not-found exception rather than the
        // generic failure the other streaming commands report.
        var notFound = (ToolFailedException ex) =>
            (Exception)new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.", ex);

        await foreach (var line in StreamOrThrowAsync(notFound, ct, [.. args]).ConfigureAwait(false))
        {
            var source = line.Stream == ToolOutputKind.Error ? LogSource.Stderr : LogSource.Stdout;
            yield return LogLine.Parse(line.Text, source, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Polls <c>stats --no-stream --format json &lt;id&gt;</c> and yields one sample per round, rather
    /// than leaving <c>nerdctl stats</c> running as a stream. Its streaming mode is a terminal display
    /// that redraws in place — the same trap <c>build</c> has (Notes/nerdctl-advanced-formats.md) —
    /// while <c>--no-stream</c> prints exactly one JSON object and exits, which is what
    /// <see cref="NerdctlJson.Parse{T}"/> already reads.
    /// <para>
    /// The delay comes after the sample, not before, so a caller that takes one value and walks away
    /// never waits: disposing the enumerator at the <c>yield</c> means <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// is never reached.
    /// </para>
    /// <para>
    /// ponytail: one process per sample, every <see cref="StatsInterval"/>. Fine for the one or two
    /// containers a detail view watches; a dashboard sampling fifty at once would want the streaming
    /// mode parsed properly instead.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            string stdout;
            try
            {
                stdout = await _cli.RunAsync(ct, "stats", "--no-stream", "--format", "json", id)
                    .ConfigureAwait(false);
            }
            catch (ToolNotFoundException ex)
            {
                throw new EngineUnreachableException($"nerdctl is not installed — cannot reach '{_backend}'.", ex);
            }
            catch (ToolFailedException ex)
            {
                // Same reasoning as StreamLogsAsync: an id nerdctl does not know about is by far the
                // most common reason `stats` exits non-zero.
                throw new ResourceNotFoundException($"Container '{id}' was not found on '{_backend}'.", ex);
            }

            IReadOnlyList<NerdctlStats> rows;
            try
            {
                rows = NerdctlJson.Parse<NerdctlStats>(stdout);
            }
            catch (JsonException ex)
            {
                throw new EngineException(
                    $"nerdctl returned output for '{_backend}' that could not be parsed: {ex.Message}", ex);
            }

            // Nothing to report means the container has stopped or been removed: `stats` answers with an
            // empty body rather than an error for an id that no longer has a running task. Ending the
            // stream is the honest reading of that — polling on would ask the same question of something
            // that can never answer again, and leave the caller's `await foreach` running forever.
            if (rows.Count == 0)
                yield break;

            foreach (var row in rows)
                yield return row.ToStats(id);

            await Task.Delay(StatsInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams <c>events --format json</c>, one record per line with a blank line between them
    /// (Notes/nerdctl-advanced-formats.md). What comes out is containerd's event vocabulary, not
    /// Docker's — see <see cref="NerdctlMap.ToEvent"/> for the mapping and for the two resource kinds
    /// this backend can never report.
    /// <para>
    /// A line that will not parse is skipped, not fatal. Unlike a listing command — where garbled output
    /// means the whole answer is wrong — this stream is the UI's activity feed: killing it over one
    /// unfamiliar record would silently stop every later event, which is a worse failure than missing
    /// the one line nobody could read.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<EngineEvent> StreamEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in StreamOrThrowAsync(Failed, ct, "events", "--format", "json")
                           .ConfigureAwait(false))
        {
            // Stderr here is nerdctl's own logrus narration, not an event record.
            if (line.Stream != ToolOutputKind.Out || string.IsNullOrWhiteSpace(line.Text))
                continue;

            IReadOnlyList<NerdctlEvent> rows;
            try
            {
                rows = NerdctlJson.Parse<NerdctlEvent>(line.Text);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var row in rows)
                yield return row.ToEvent();
        }
    }
}
