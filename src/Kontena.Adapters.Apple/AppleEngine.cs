using System.Runtime.CompilerServices;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Shell;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// CEAL over Apple's native macOS <c>container</c> runtime (KON-31). Each container is its own
/// lightweight VM and there is no daemon socket, so everything here comes from parsing what the CLI
/// prints — the formats were captured against a real install rather than read from its docs (Depot
/// kontena/Notes/apple-container-cli-formats.md).
/// <para>
/// Two kinds of <see cref="NotSupportedException"/> live in this file and they mean different things:
/// </para>
/// <list type="bullet">
/// <item><description><b>The runtime cannot do it.</b> <c>container</c> 1.2 has no pause, no Compose,
/// no event stream, and no way to attach a running container to a network — those subcommands do not
/// exist (<see cref="PauseUnsupported"/>, <see cref="ComposeUnsupported"/>,
/// <see cref="EventsUnsupported"/>, <see cref="NetworkAttachUnsupported"/>). Where a capability flag
/// exists for it, <see cref="Capabilities"/> already says so, and the UI does not offer it.</description></item>
/// <item><description><b>The credential boundary.</b> A registry login cannot be used for a single
/// operation without the runtime keeping the secret, so both paths that would need one are refused
/// (<see cref="RegistryCredentialUnsupported"/>).</description></item>
/// </list>
/// </summary>
internal sealed class AppleEngine(AppleCli cli, string backend, string displayName) : IContainerEngine
{
    private const string PauseUnsupported =
        "Apple container cannot pause a container: each one is its own virtual machine and the CLI has " +
        "no pause/resume subcommand (verified against 1.2.2).";

    private const string ComposeUnsupported =
        "Apple container has no Compose support: the CLI has no compose subcommand (verified against 1.2.2).";

    private const string EventsUnsupported =
        "Apple container does not stream engine events: the CLI has no events subcommand (verified " +
        "against 1.2.2). Lists refresh on demand instead.";

    private const string NetworkAttachUnsupported =
        "Apple container attaches networks when a container is created, not afterwards: the CLI has no " +
        "network connect/disconnect subcommand (verified against 1.2.2).";

    private const string RegistryCredentialUnsupported =
        "Apple container cannot use a registry login for a single operation: `container image pull` takes " +
        "no credentials at all, and `container registry login` works only by storing the secret in the " +
        "runtime's own credential store (verified against 1.2.2). Kontena keeps registry secrets in the " +
        "OS keychain and checks a login without saving it, which this runtime offers no way to do — so " +
        "it is refused rather than quietly writing your password somewhere else. Pull from public " +
        "registries works normally.";

    private const string RestartPolicyUnsupported =
        "Apple container cannot restart a container automatically: `container run` has no restart-policy " +
        "flag (verified against 1.2.2) — only --rm, which is the opposite. Create it with the policy set " +
        "to 'no'; anything else would be accepted here and silently never happen.";

    /// <summary>
    /// How often <see cref="StreamStatsAsync"/> takes a sample. The same two seconds the nerdctl plugin
    /// uses: each sample is a process, and a graph that moves faster than a person reads costs more than
    /// it shows.
    /// </summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    public string Backend => backend;

    /// <summary>
    /// What this runtime can do. <see cref="EngineCapabilities.Rootless"/> is true and not a
    /// simplification: containers run in per-container VMs launched by a user-level launchd service, so
    /// there is no root daemon on the host to speak of — that is the runtime's whole design.
    /// <para>
    /// Everything this adapter can do now says so. What stays false is what the runtime itself lacks:
    /// Compose and an event stream do not exist here, and no PR will change that. GPU passthrough is
    /// false for every backend Kontena has.
    /// </para>
    /// </summary>
    public EngineCapabilities Capabilities => new()
    {
        Rootless = true,
        SupportsBuild = true,
        SupportsCompose = false,
        SupportsExec = true,

        // The one flag this runtime is missing rather than this adapter: `container run` has no
        // restart-policy option at all — see RestartPolicyUnsupported.
        SupportsRestartPolicy = false,
        SupportsPrune = true,
        SupportsVolumeBrowse = true,
        SupportsVolumeTransfer = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = false,
    };

    public async ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var versions = await cli
            .ListAsync<AppleVersion>(ct, "system", "version", "--format", "json")
            .ConfigureAwait(false);

        return new BackendInfo
        {
            Backend = backend,
            DisplayName = displayName,
            Kind = "container engine",
            Version = AppleMap.Version(versions),

            // There is no socket or URL to name: the CLI talks to a launchd-managed apiserver over XPC.
            // Saying so is more use in the title bar than an empty field or an invented path.
            Endpoint = "apiserver (XPC)",
            ConnectionState = EngineConnectionState.Connected,
        };
    }

    /// <summary>
    /// Asks the apiserver whether it is up. <c>system status</c> is the cheapest command that fails when
    /// the launchd service is not running — which, unlike a missing install, is an ordinary state here:
    /// <c>container system start</c> is a thing users do.
    /// </summary>
    public async ValueTask PingAsync(CancellationToken ct = default)
    {
        try
        {
            await cli.RunAsync(ct, "system", "status").ConfigureAwait(false);
        }
        catch (ToolNotFoundException error)
        {
            throw new EngineUnreachableException("Apple container is not installed.", error);
        }
    }

    // ── Containers ──────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default)
    {
        var containers = await ListRawContainersAsync(all, ct).ConfigureAwait(false);
        return [.. containers.Select(c => AppleMap.Container(c, backend))];
    }

    /// <summary>
    /// The raw list, shared by the container, volume and network listings: a volume's "used by" and a
    /// network's "attached containers" are not fields this CLI prints, they are answered by looking at
    /// what the containers mount and join.
    /// </summary>
    private ValueTask<IReadOnlyList<AppleContainer>> ListRawContainersAsync(bool all, CancellationToken ct) =>
        all
            ? cli.ListAsync<AppleContainer>(ct, "list", "--all", "--format", "json")
            : cli.ListAsync<AppleContainer>(ct, "list", "--format", "json");

    /// <summary>
    /// <c>inspect</c> prints the same record the list does, filtered by id — so this is one call, and the
    /// mapping reads more fields off the same shape rather than a second model.
    /// </summary>
    public async ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        var containers = await cli.ListAsync<AppleContainer>(ct, "inspect", id).ConfigureAwait(false);

        // The CLI errors on an unknown id, so an empty array means it answered about nothing at all —
        // reported as the same missing-resource condition rather than returning a blank detail page.
        return containers.Count > 0
            ? AppleMap.Inspect(containers[0])
            : throw new ResourceNotFoundException($"Container not found: {id}");
    }

    public async ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "start", id).ConfigureAwait(false);

    public async ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "stop", id).ConfigureAwait(false);

    /// <summary>
    /// Stop, then start. <c>container</c> has no <c>restart</c> subcommand, and doing it in two steps
    /// here rather than leaving the button unimplemented keeps the restart honest: if the stop fails,
    /// nothing is started and the caller sees why.
    /// </summary>
    public async ValueTask RestartContainerAsync(string id, CancellationToken ct = default)
    {
        await StopContainerAsync(id, ct).ConfigureAwait(false);
        await StartContainerAsync(id, ct).ConfigureAwait(false);
    }

    public async ValueTask RemoveContainerAsync(
        string id, bool force = false, CancellationToken ct = default)
    {
        if (force)
            await cli.RunAsync(ct, "delete", "--force", id).ConfigureAwait(false);
        else
            await cli.RunAsync(ct, "delete", id).ConfigureAwait(false);
    }

    /// <summary>Not available — see <see cref="PauseUnsupported"/>.</summary>
    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(PauseUnsupported);

    /// <summary>Not available — see <see cref="PauseUnsupported"/>.</summary>
    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException(PauseUnsupported);

    /// <summary>
    /// Creates a container, and starts it unless the request says otherwise — <c>run --detach</c> for
    /// the first, <c>create</c> for the second, since this CLI has both.
    /// <para>
    /// The id comes from the <b>last</b> line of output, not the first: <c>run</c> narrates its progress
    /// ("[6/6] Starting container") before printing the name, and taking the first line would hand every
    /// caller a progress message as an id.
    /// </para>
    /// </summary>
    public async ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        var architecture = await ForeignArchitectureAsync(request.Image, ct).ConfigureAwait(false);

        var stdout = await cli
            .RunAsync(ct, [.. CreateArguments(request, architecture)])
            .ConfigureAwait(false);

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        return lines.Length > 0
            ? lines[^1].Trim()
            : throw new EngineException("Apple container created the container but did not name it.");
    }

    /// <summary>
    /// The architecture to ask for, or null when this host's own will do.
    /// <para>
    /// Without <c>--arch</c> the CLI creates for the host, and an image that has no variant for it
    /// fails with the bare line <c>Error: platform linux/arm64</c> — which is what an amd64-only image
    /// such as <c>mcr.microsoft.com/mssql/server</c> is (KON-369). Naming the architecture the image
    /// actually carries runs it under emulation instead, which this runtime does.
    /// </para>
    /// <para>
    /// Every failure to read the image answers null — it is not here yet, or its inspect came back in a
    /// shape this adapter cannot parse. Both leave the create exactly as it was before this method
    /// existed, and the create's own error is the one worth reading; refusing here would turn a
    /// question about the image into a failure to create anything at all.
    /// </para>
    /// </summary>
    private async ValueTask<string?> ForeignArchitectureAsync(string reference, CancellationToken ct)
    {
        IReadOnlyList<AppleImage> images;

        try
        {
            images = await cli.ListAsync<AppleImage>(ct, "image", "inspect", reference).ConfigureAwait(false);
        }
        catch (EngineException)
        {
            return null;
        }

        var variants = (images.Count > 0 ? images[0].Variants ?? [] : [])
            .Where(v => v.IsRealPlatform)
            .ToList();

        if (variants.Count == 0 || variants.Exists(v => string.Equals(
                v.Platform!.Architecture, ToolPlatform.Architecture, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return variants[0].Platform!.Architecture;
    }

    private static List<string> CreateArguments(CreateContainerRequest request, string? architecture)
    {
        // `create` makes one without starting it; `run --detach` does both. Creating and then starting
        // separately would work too, but it turns one failure into two states to unwind.
        var arguments = request.Start ? new List<string> { "run", "--detach" } : ["create"];

        if (request.RestartPolicy is not RestartPolicy.No)
            throw new NotSupportedException(RestartPolicyUnsupported);

        if (architecture is { Length: > 0 } arch)
        {
            arguments.Add("--arch");
            arguments.Add(arch);
        }

        if (request.Name is { Length: > 0 } name)
        {
            arguments.Add("--name");
            arguments.Add(name);
        }

        foreach (var port in request.Ports)
        {
            // Publishing needs a host port to publish on. A binding without one describes a port the
            // image exposes, which this CLI takes from the image itself.
            if (port.HostPort is not { } hostPort)
                continue;

            arguments.Add("--publish");
            arguments.Add($"{hostPort}:{port.ContainerPort}/{port.Protocol}");
        }

        foreach (var (key, value) in request.Environment)
        {
            arguments.Add("--env");
            arguments.Add($"{key}={value}");
        }

        foreach (var mount in request.Mounts)
        {
            // `--volume` has no read-only form on this CLI; `--mount` is the only flag that does, so
            // a read-only mount goes through the longer spelling rather than silently becoming
            // writable.
            if (mount.ReadOnly)
            {
                arguments.Add("--mount");
                arguments.Add($"type={mount.Type},source={mount.Source},target={mount.Target},readonly");
            }
            else
            {
                arguments.Add("--volume");
                arguments.Add($"{mount.Source}:{mount.Target}");
            }
        }

        if (request.Network is { Length: > 0 } network)
        {
            arguments.Add("--network");
            arguments.Add(network);
        }

        // `--entrypoint` here takes a single command, unlike Docker's array. The remaining parts keep
        // their meaning by moving to the front of the command: `--entrypoint foo image a b` runs
        // `foo a b`, which is exactly what a multi-part entry point means.
        if (request.Entrypoint.Count > 0)
        {
            arguments.Add("--entrypoint");
            arguments.Add(request.Entrypoint[0]);
        }

        if (request.WorkingDirectory is { Length: > 0 } workingDirectory)
        {
            arguments.Add("--workdir");
            arguments.Add(workingDirectory);
        }

        if (request.User is { Length: > 0 } user)
        {
            arguments.Add("--user");
            arguments.Add(user);
        }

        foreach (var (key, value) in request.Labels)
        {
            arguments.Add("--label");
            arguments.Add($"{key}={value}");
        }

        arguments.Add(request.Image);

        arguments.AddRange(request.Entrypoint.Skip(1));
        arguments.AddRange(request.Command);

        return arguments;
    }

    /// <summary>
    /// Runs a command and hands back its exit code. <c>container exec</c> exits with the code of the
    /// process it ran, so unlike nerdctl there is nothing to dig out of an error line — but it also
    /// means a refusal has to be told apart from a command that simply failed, which
    /// <see cref="AppleCli.RunForExitCodeAsync"/> does.
    /// </summary>
    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        cli.RunForExitCodeAsync(ct, [.. ExecArguments(id, request, interactive: false)]);

    /// <summary>
    /// Opens an interactive shell in the container, in a real pseudo-terminal.
    /// <para>
    /// Not through <see cref="IToolRunner"/>: that seam starts a process and reads its output, with no
    /// way to write to its stdin and no PTY — which is exactly why the nerdctl plugin has to refuse
    /// this. <see cref="PtyShellSession"/> is the seam that does have one, and the command it starts is
    /// an ordinary <c>container exec -i -t</c>.
    /// </para>
    /// <para>
    /// The binary is resolved rather than named: the pseudo-terminal spawns it directly, so it must get
    /// the path the tool runner would have used instead of trusting whatever PATH the app inherited.
    /// </para>
    /// </summary>
    public async ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default)
    {
        var executable = await cli.LocateAsync(ct).ConfigureAwait(false);
        var command = new PtyCommand(executable, ExecArguments(id, request, interactive: true));

        // 80x24 is where every terminal starts; the view resizes it as soon as it has a size of its own.
        // The working directory is the host's, and irrelevant: -w decides where the process runs inside
        // the container.
        return await PtyShellSession
            .StartAsync(command, Path.GetTempPath(), columns: 80, rows: 24, supportDirectory: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the argument list both exec paths share. <c>-i</c> and <c>-t</c> only go on the
    /// interactive one: a one-shot exec that asked for a TTY would get its output line-buffered through
    /// a terminal and its stderr folded into stdout, for a caller that only wants an exit code.
    /// </summary>
    private static List<string> ExecArguments(string id, ExecRequest request, bool interactive)
    {
        var arguments = new List<string> { "exec" };

        if (interactive)
        {
            arguments.Add("--interactive");

            // The request's own Tty flag is honoured, but an interactive session without one is a shell
            // with no prompt and no line editing — so this asks for it whenever the caller did.
            if (request.Tty)
                arguments.Add("--tty");
        }

        if (request.WorkingDirectory is { Length: > 0 } directory)
        {
            arguments.Add("--workdir");
            arguments.Add(directory);
        }

        arguments.Add(id);
        arguments.AddRange(request.Command);

        return arguments;
    }

    /// <summary>Removes every stopped container.</summary>
    public ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        PruneAsync(usage => usage.Containers, ct, "prune");

    /// <summary>
    /// Runs one of the prune commands and reports what it did.
    /// <para>
    /// The count is how many ids the command printed — it lists one per line after its summary. The
    /// byte figure is deliberately <b>not</b> read from that summary: it is a localised sentence
    /// ("Reclaimed 1,37 GB in disk space" on a Dutch machine), and when nothing was removed the CLI
    /// writes the word "Zero" where the number goes. It comes from the drop in what
    /// <c>system df</c> reports for the category instead — integers, in bytes, in any locale.
    /// </para>
    /// <para>
    /// <c>sizeInBytes</c> and not <c>reclaimable</c>: pruning containers makes the image they used
    /// reclaimable, so that figure <em>rises</em> across a prune that freed nothing of it.
    /// </para>
    /// </summary>
    private async ValueTask<PruneResult> PruneAsync(
        Func<AppleDiskUsage, AppleDiskUsageEntry?> category, CancellationToken ct, params string[] args)
    {
        var before = await DiskUsageAsync(category, ct).ConfigureAwait(false);
        var stdout = await cli.RunAsync(ct, args).ConfigureAwait(false);
        var after = await DiskUsageAsync(category, ct).ConfigureAwait(false);

        return new PruneResult(PrunedCount(stdout), Math.Max(0, before - after));
    }

    private async ValueTask<long> DiskUsageAsync(
        Func<AppleDiskUsage, AppleDiskUsageEntry?> category, CancellationToken ct)
    {
        var usage = await cli
            .GetAsync<AppleDiskUsage>(ct, "system", "df", "--format", "json")
            .ConfigureAwait(false);

        return usage is null ? 0 : category(usage)?.SizeInBytes ?? 0;
    }

    /// <summary>
    /// Counts the ids a prune printed. Everything except its summary line is one removed thing —
    /// <c>network prune</c> prints no summary at all, having nothing to reclaim, so the line is skipped
    /// by what it says rather than by its position.
    /// </summary>
    private static int PrunedCount(string stdout) =>
        stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => !line.TrimStart().StartsWith("Reclaimed", StringComparison.OrdinalIgnoreCase));

    // ── Images ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default)
    {
        var images = await cli
            .ListAsync<AppleImage>(ct, "image", "list", "--format", "json")
            .ConfigureAwait(false);

        // "In use" is not a field this CLI prints either; it is whether a container was created from
        // that reference, which the container list answers.
        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);
        var inUse = containers
            .Select(c => c.Configuration?.Image?.Reference)
            .Where(reference => !string.IsNullOrEmpty(reference))
            .ToHashSet(StringComparer.Ordinal)!;

        return [.. images.Select(image => AppleMap.Image(image, inUse!))];
    }

    /// <summary>
    /// Pulls an image, reporting what the CLI narrates.
    /// <para>
    /// The progress lines carry no usable byte figures, so <see cref="PullProgress.Current"/> and
    /// <see cref="PullProgress.Total"/> stay null and the line itself is the status — the same choice
    /// the nerdctl plugin makes. Look at what there is to parse:
    /// <c>[1/2] Fetching image 47% (64 of 111 blobs, 91,7/191,6 MB, 17,3 MB/s)</c>. On this machine a
    /// comma is the decimal separator and a dot groups thousands ("2.192 entries"), the two sides of the
    /// fraction can carry different units ("320 KB/191,6 MB"), and all of it moves with the host's
    /// locale. A number read wrong there is a progress bar that jumps or stalls, which is worse than a
    /// line of text that is simply true.
    /// </para>
    /// <para>
    /// Everything the CLI prints here goes to <b>stderr</b>; stdout stays empty for the whole pull.
    /// <c>--progress plain</c> is passed so the shape does not depend on whether the process happened to
    /// get a terminal.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (credential is not null)
            throw new NotSupportedException(RegistryCredentialUnsupported);

        var lines = cli.StreamAsync(ct, "image", "pull", "--progress", "plain", reference);

        await foreach (var line in lines.ConfigureAwait(false))
            yield return new PullProgress(reference, line.Text, null, null);
    }

    /// <summary>
    /// Streams <c>container image push</c>, the counterpart to <see cref="PullImageAsync"/> — same
    /// free-form lines on stderr, same reason <see cref="PushProgress.Current"/> and
    /// <see cref="PushProgress.Total"/> stay <c>null</c>.
    /// <para>
    /// No <c>--progress plain</c> here, unlike the pull. That flag has been verified on
    /// <c>image pull</c> and not on <c>image push</c>, and this CLI rejects a flag it does not know
    /// rather than ignoring it — which would turn every push into an argument error. Without a terminal
    /// the plain shape is what it prints anyway.
    /// </para>
    /// <para>
    /// A <paramref name="credential"/> is refused for the same reason a pull's is
    /// (<see cref="RegistryCredentialUnsupported"/>), which leaves push to a registry that accepts
    /// anonymous writes.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<PushProgress> PushImageAsync(
        string reference, RegistryCredential? credential = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (credential is not null)
            throw new NotSupportedException(RegistryCredentialUnsupported);

        var lines = cli.StreamAsync(ct, "image", "push", reference);

        await foreach (var line in lines.ConfigureAwait(false))
            yield return new PushProgress(reference, line.Text, null, null);
    }

    /// <summary>
    /// Not available — see <see cref="RegistryCredentialUnsupported"/>. The CLI can log in, but only by
    /// keeping the credential, and this method exists precisely to check one without keeping it.
    /// </summary>
    public ValueTask VerifyRegistryLoginAsync(
        RegistryCredential credential, CancellationToken ct = default) =>
        throw new NotSupportedException(RegistryCredentialUnsupported);

    /// <summary>
    /// Builds an image through the runtime's own BuildKit builder, streaming its output.
    /// <para>
    /// Unlike the nerdctl plugin, this needs no reachability check first. There, <c>build</c> exists
    /// whether or not a buildkitd does, so the plugin looks for the socket before promising anything;
    /// here the builder is part of the runtime and the first build starts it — <c>builder status</c>
    /// reports nothing until then, and the build works anyway.
    /// </para>
    /// <para>
    /// A failure is yielded rather than thrown: the build console is where a failed build belongs, and
    /// the caller has already shown twenty lines of it by the time the last one says why it stopped.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(request.ContextPath))
        {
            var missing = $"Build context not found: {request.ContextPath}";
            yield return new BuildProgress(missing, missing);
            yield break;
        }

        var lines = cli.StreamAsync(ct, [.. BuildArguments(request)]);
        var failed = false;

        // The tool seam signals a non-zero exit by throwing once the output is drained, which cannot be
        // caught around a `yield` — so the enumerator is stepped by hand and the failure becomes the
        // last line of the console instead of an exception out of an async stream.
        await using var enumerator = lines.GetAsyncEnumerator(ct);

        while (true)
        {
            string? failure = null;
            var more = false;

            try
            {
                more = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ToolFailedException error)
            {
                failure = error.Complaint.Length > 0 ? error.Complaint : error.Message;
            }

            // Yielding cannot happen inside the catch, so the failure is carried out of it first.
            if (failure is not null)
            {
                failed = true;
                yield return new BuildProgress(failure, failure);
                break;
            }

            if (!more)
                break;

            yield return new BuildProgress(enumerator.Current.Text);
        }

        if (!failed)
            yield return new BuildProgress($"Built {request.Tag}");
    }

    /// <summary>
    /// Every flag here exists on this CLI — <c>--target</c> and <c>--pull</c> included, which is worth
    /// saying because the neutral request carries both and a silently dropped one is a build that did
    /// something other than what was asked.
    /// </summary>
    private static List<string> BuildArguments(BuildRequest request)
    {
        var dockerfile = string.IsNullOrWhiteSpace(request.Dockerfile) ? "Dockerfile" : request.Dockerfile;

        List<string> arguments =
        [
            "build",

            // Plain so the output does not depend on whether the process was given a terminal, and so
            // the caller's step parser sees BuildKit's own "#3 [2/4] RUN ..." lines.
            "--progress", "plain",
            "--file", Path.IsPathRooted(dockerfile) ? dockerfile : Path.Combine(request.ContextPath, dockerfile),
            "--tag", request.Tag,
        ];

        if (!string.IsNullOrWhiteSpace(request.Target))
            arguments.AddRange(["--target", request.Target]);

        if (request.NoCache)
            arguments.Add("--no-cache");

        if (request.Pull)
            arguments.Add("--pull");

        foreach (var (key, value) in request.BuildArgs)
            arguments.AddRange(["--build-arg", $"{key}={value}"]);

        arguments.Add(request.ContextPath);

        return arguments;
    }

    /// <summary>
    /// Removes an image. There is no force flag here; an image a container still uses is refused with a
    /// non-zero exit, which reaches the caller as an exception rather than as a row that stays put.
    /// </summary>
    public async ValueTask RemoveImageAsync(
        string id, bool force = false, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "image", "delete", id).ConfigureAwait(false);

    /// <summary>
    /// Reads an image's baked-in config to pre-fill the Run flow — as far as this CLI reports it, which
    /// is the environment and nothing else.
    /// <para>
    /// <b>Ports and volumes are not missing here, they are missing there.</b> Captured against
    /// <c>nginx:alpine</c>, which declares both: no variant's config carries an <c>ExposedPorts</c> or
    /// <c>Volumes</c> key at all. The Run dialog only adds rows for what it is given, so an image's
    /// environment is pre-filled and its ports are typed by hand — nothing on screen claims the image
    /// exposes none.
    /// </para>
    /// </summary>
    public async ValueTask<ImageConfig?> InspectImageAsync(
        string reference, CancellationToken ct = default)
    {
        IReadOnlyList<AppleImage> images;

        try
        {
            images = await cli.ListAsync<AppleImage>(ct, "image", "inspect", reference).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            // The contract's own answer for an image that is not here: null, not an error. The Run flow
            // asks about whatever was typed in the box, so "no such image yet" is an ordinary state.
            return null;
        }

        return images.Count > 0 ? AppleMap.ImageConfig(images[0]) : null;
    }

    /// <summary>Gives an image a second name.</summary>
    public async ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "image", "tag", id, newTag).ConfigureAwait(false);

    /// <summary>
    /// Removes unused images. The <paramref name="allUnused"/> distinction Docker draws — dangling only,
    /// versus every image no container uses — does not exist here: <c>image prune</c> takes no such flag
    /// and always does the second. Honouring the parameter would mean claiming a narrower sweep than the
    /// one that actually runs.
    /// </summary>
    public ValueTask<PruneResult> PruneImagesAsync(
        bool allUnused = true, CancellationToken ct = default) =>
        PruneAsync(usage => usage.Images, ct, "image", "prune");

    // ── Volumes ─────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        var volumes = await cli
            .ListAsync<AppleVolume>(ct, "volume", "list", "--format", "json")
            .ConfigureAwait(false);

        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);

        return [.. volumes.Select(volume => AppleMap.Volume(volume, containers))];
    }

    /// <summary>
    /// Creates a volume and reads it back, so the row the caller gets is the engine's own answer rather
    /// than an echo of the request — the mountpoint in particular is a path only the runtime knows.
    /// </summary>
    public async ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default)
    {
        await cli.RunAsync(ct, "volume", "create", request.Name).ConfigureAwait(false);

        var volumes = await ListVolumesAsync(ct).ConfigureAwait(false);

        return volumes.FirstOrDefault(v => v.Name == request.Name)
            ?? new VolumeSummary { Name = request.Name };
    }

    /// <summary>
    /// Removes a volume. There is no force flag on this CLI, and a volume a container still holds is
    /// refused — with a non-zero exit, so the refusal arrives as an exception rather than as silence.
    /// </summary>
    public async ValueTask RemoveVolumeAsync(
        string name, bool force = false, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "volume", "delete", name).ConfigureAwait(false);

    /// <summary>
    /// Lists what is inside a volume, by mounting it read-only into a throwaway container and asking
    /// that container.
    /// <para>
    /// The Docker adapter does this without running anything — it creates a container and reads its
    /// filesystem through the archive endpoint. This CLI has no such endpoint, so here the container
    /// genuinely <b>runs</b>, for as long as one <c>find</c> takes, and <c>--rm</c> takes it away
    /// afterwards. That is the honest cost of the same feature on a runtime driven by its CLI.
    /// </para>
    /// <para>
    /// <c>stat</c> rather than <c>ls</c>: its format string is a contract, while <c>ls -l</c> output
    /// varies with locale, column widths and the age of a file. Every entry comes back as
    /// <c>type|size|mtime|path</c>, which cannot be misread.
    /// </para>
    /// </summary>
    public async ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default)
    {
        var target = NormalizeBrowsePath(path);
        var image = await SmallestLocalImageAsync(ct).ConfigureAwait(false);

        var listing = $"find {MountPoint}{target} -maxdepth 1 -mindepth 1 -exec stat -c '%F|%s|%Y|%n' {{}} +";

        string stdout;

        try
        {
            stdout = await cli.RunAsync(
                ct,
                "run", "--rm",
                "--volume", $"{name}:{MountPoint}",
                image,
                "sh", "-c", listing).ConfigureAwait(false);
        }
        catch (EngineException error) when (error.Message.Contains(
            "No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            // `find` says this, and it is the one failure with an ordinary cause: someone opened a
            // folder that has since been deleted. Everything the CLI printed before it is the VM
            // starting up, which is noise in front of a one-line answer.
            throw new ResourceNotFoundException(
                $"There is no '{(target.Length == 0 ? "/" : target)}' in volume '{name}'.", error);
        }

        var entries = new List<VolumeEntry>();
        var truncated = false;

        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (AppleMap.VolumeEntry(line, MountPoint + target) is not { } entry)
                continue;

            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            entries.Add(entry);
        }

        return new VolumeListing(target.Length == 0 ? "/" : target, entries, truncated);
    }

    /// <summary>
    /// An image to mount the volume into. The smallest one, because it has to start: on this runtime
    /// that means booting a virtual machine, and a 4 MB image boots faster than a 200 MB one.
    /// </summary>
    private async ValueTask<string> SmallestLocalImageAsync(CancellationToken ct)
    {
        var images = await ListImagesAsync(ct).ConfigureAwait(false);

        return images
            .Where(i => i.Tag != "<none>")
            .OrderBy(i => i.SizeBytes)
            .Select(i => $"{i.Repository}:{i.Tag}")
            .FirstOrDefault()
            ?? throw new EngineException(
                "Reading a volume needs an image to mount it into, and this engine has none. "
                + "Pull any image first — nothing of your own runs in it.");
    }

    /// <summary>Where the volume is mounted inside the throwaway container.</summary>
    private const string MountPoint = "/kontena-volume";

    /// <summary>Where the staging directory is mounted inside the throwaway container.</summary>
    private const string StagePoint = "/kontena-stage";

    /// <summary>
    /// Packs a volume into a tar on the host, by mounting both the volume and the archive's directory
    /// into a throwaway container and running <c>tar</c> there. Running it inside the container is
    /// what keeps uid, gid and mode on the files — see <see cref="IContainerEngine.ExportVolumeAsync"/>.
    /// </summary>
    public async ValueTask ExportVolumeAsync(
        string name, string archivePath, CancellationToken ct = default)
    {
        var (directory, file) = SplitArchivePath(archivePath);
        var image = await SmallestLocalImageAsync(ct).ConfigureAwait(false);

        // Every volume on this runtime carries a lost+found nobody created — they are ext4 images.
        // Only the root one is excluded, so a directory a user happens to name that deeper survives.
        var pack = $"tar -cf {StagePoint}/{file} --exclude=./lost+found -C {MountPoint} .";

        await cli.RunAsync(
            ct,
            "run", "--rm",
            "--volume", $"{name}:{MountPoint}",
            "--volume", $"{directory}:{StagePoint}",
            image,
            "sh", "-c", pack).ConfigureAwait(false);
    }

    /// <summary>
    /// Unpacks an archive into a volume, as root inside the container, so the ownership recorded in
    /// the tar is restored rather than replaced by whoever is logged in on the host.
    /// </summary>
    public async ValueTask ImportVolumeAsync(
        string name, string archivePath, CancellationToken ct = default)
    {
        var (directory, file) = SplitArchivePath(archivePath);
        var image = await SmallestLocalImageAsync(ct).ConfigureAwait(false);

        var unpack = $"tar -xf {StagePoint}/{file} -C {MountPoint}";

        await cli.RunAsync(
            ct,
            "run", "--rm",
            "--volume", $"{name}:{MountPoint}",
            "--volume", $"{directory}:{StagePoint}",
            image,
            "sh", "-c", unpack).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a host archive path into the directory to mount and the file name inside it. The
    /// container never sees the host path, only the mount point, so the two have to be handed over
    /// separately.
    /// </summary>
    private static (string Directory, string File) SplitArchivePath(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);

        return (
            Path.GetDirectoryName(full)
                ?? throw new EngineException($"'{archivePath}' has no directory to mount."),
            Path.GetFileName(full));
    }

    /// <summary>
    /// A directory with more entries than this is listed up to here and says so. The same ceiling the
    /// Docker adapter uses, for the same reason: past a few thousand rows the wait is the feature.
    /// </summary>
    private const int MaxEntries = 5_000;

    /// <summary>
    /// The path inside the volume: absolute, no trailing slash, and with no way out of the mount.
    /// <c>..</c> is resolved here rather than passed on — the command runs inside a container whose own
    /// filesystem sits right outside that mount point.
    /// </summary>
    internal static string NormalizeBrowsePath(string path)
    {
        var stack = new List<string>();

        foreach (var part in (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);

                continue;
            }

            stack.Add(part);
        }

        return stack.Count == 0 ? string.Empty : "/" + string.Join('/', stack);
    }

    /// <summary>Removes every volume no container uses.</summary>
    public ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        PruneAsync(usage => usage.Volumes, ct, "volume", "prune");

    // ── Networks ────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        var networks = await cli
            .ListAsync<AppleNetwork>(ct, "network", "list", "--format", "json")
            .ConfigureAwait(false);

        var containers = await ListRawContainersAsync(all: true, ct).ConfigureAwait(false);

        return [.. networks.Select(network => AppleMap.Network(network, containers))];
    }

    /// <summary>
    /// Creates a network and reads it back — the subnet is assigned by the runtime, so the row is worth
    /// having from it rather than from the request.
    /// <para>
    /// The request's driver is not passed on. This CLI calls the same thing a <c>--plugin</c>, defaults
    /// it to <c>container-network-vmnet</c>, and would reject "bridge" — the neutral model's default,
    /// and Docker's word for what this already is.
    /// </para>
    /// </summary>
    public async ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default)
    {
        var arguments = new List<string> { "network", "create" };

        if (request.Subnet is { Length: > 0 } subnet)
        {
            arguments.Add("--subnet");
            arguments.Add(subnet);
        }

        arguments.Add(request.Name);

        await cli.RunAsync(ct, [.. arguments]).ConfigureAwait(false);

        var networks = await ListNetworksAsync(ct).ConfigureAwait(false);

        return networks.FirstOrDefault(n => n.Name == request.Name)
            ?? new NetworkSummary { Id = request.Name, Name = request.Name };
    }

    /// <summary>
    /// Removes a network. A network a container is still attached to is refused, non-zero, naming the
    /// container that holds it.
    /// </summary>
    public async ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        await cli.RunAsync(ct, "network", "delete", id).ConfigureAwait(false);

    /// <summary>Not available — see <see cref="NetworkAttachUnsupported"/>.</summary>
    public ValueTask ConnectNetworkAsync(
        string containerId, string networkId, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    /// <summary>Not available — see <see cref="NetworkAttachUnsupported"/>.</summary>
    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default) =>
        throw new NotSupportedException(NetworkAttachUnsupported);

    // ── Compose ─────────────────────────────────────────────────────────────

    /// <summary>Not available — see <see cref="ComposeUnsupported"/>.</summary>
    public IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(ComposeUnsupported);

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams a container's log.
    /// <para>
    /// Everything arrives on one channel: <c>container logs</c> writes the container's stderr to its own
    /// stdout, so there is no split to map and every entry is reported as
    /// <see cref="LogSource.Stdout"/>. Claiming otherwise would put a colour on a line that means
    /// nothing.
    /// </para>
    /// <para>
    /// There is no <c>--timestamps</c> flag either, so the time on an entry is when Kontena read the
    /// line. <see cref="LogLine.Parse"/> still takes a stamp the container printed itself, which is the
    /// only case where the time is the container's own.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string[] arguments = follow ? ["logs", "--follow", id] : ["logs", id];

        await foreach (var line in cli.StreamAsync(ct, arguments).ConfigureAwait(false))
            yield return LogLine.Parse(line.Text, LogSource.Stdout, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Samples a container's resource usage.
    /// <para>
    /// Polled, because there is nothing to stream: <c>container stats --format json</c> prints one array
    /// and exits — the continuously updating display the CLI is documented to have is the table format
    /// only. So this runs the snapshot on a timer, at the same two-second cadence the nerdctl plugin
    /// uses.
    /// </para>
    /// <para>
    /// The CPU figure is computed here rather than read: this CLI reports <c>cpuUsageUsec</c>, a counter
    /// that only goes up, where Docker and nerdctl hand over a percentage. It is the rise in that
    /// counter over the wall-clock time between two samples, which is why <b>the first sample reports
    /// zero</b> — there is no earlier one to subtract, and inventing a number for it would be a spike on
    /// every graph the moment it opens.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        AppleStats? previous = null;
        var previousAt = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var samples = await cli
                .ListAsync<AppleStats>(ct, "stats", "--format", "json", "--no-stream", id)
                .ConfigureAwait(false);

            // A container that stopped between two samples prints nothing rather than erroring. That is
            // the end of the stream, not a gap in it.
            if (samples.Count == 0)
                yield break;

            var current = samples[0];
            var now = DateTimeOffset.UtcNow;

            yield return AppleMap.Stats(current, previous, now - previousAt, id);

            previous = current;
            previousAt = now;

            try
            {
                await Task.Delay(StatsInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>Not available — see <see cref="EventsUnsupported"/>.</summary>
    public IAsyncEnumerable<EngineEvent> StreamEventsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(EventsUnsupported);
}
