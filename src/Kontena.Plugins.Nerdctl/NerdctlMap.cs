using Kontena.Sdk.Models;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// Turns nerdctl's own JSON shapes (<see cref="NerdctlContainer"/> and siblings) into the CEAL's engine-neutral
/// models — the same relationship <c>Kontena.Adapters.Docker.DockerEngine</c> has to Docker.DotNet's
/// response types, followed here field-for-field wherever nerdctl's inspect payload actually matches
/// Docker's (KON-141). Every method takes one already-deserialized row; NDJSON splitting and array
/// parsing are the caller's job (<see cref="NerdctlJson"/>), same separation Docker's mapper keeps
/// from Docker.DotNet's own deserialization.
/// </summary>
public static class NerdctlMap
{
    /// <summary>
    /// Maps one <c>ps</c> row. Two things nerdctl gives that are not directly usable are handled here
    /// rather than passed through:
    /// <list type="bullet">
    /// <item>
    /// <description><see cref="NerdctlContainer.Names"/> is <c>k8s://namespace/pod/container</c> for
    /// every CRI-managed container — not a name a user recognises. The last path segment (the
    /// container name) becomes <see cref="ContainerSummary.Name"/>; the full string survives under a
    /// synthetic label rather than in <see cref="ContainerSummary.Status"/>, because <c>Status</c> is
    /// already the bare engine status ("Up"/"Created") the UI shows elsewhere, and overwriting it would
    /// lose that.</description>
    /// </item>
    /// <item>
    /// <description><see cref="NerdctlContainer.Command"/> is quoted and ellipsis-truncated — not a
    /// real command line, so it is never read here at all. <see cref="ContainerSummary"/> has no
    /// command field; the real one lives on <see cref="ContainerInspect.Command"/>, built by
    /// <see cref="ToInspect"/> from <c>inspect</c>'s <c>Path</c>/<c>Args</c>.</description>
    /// </item>
    /// </list>
    /// </summary>
    public static ContainerSummary ToSummary(this NerdctlContainer container, string backend)
    {
        // "kontena.nerdctl.names" is our own synthetic key, added after the parsed labels so it can
        // only ever be overwritten by a real label of that exact name — vanishingly unlikely (real CRI
        // labels are all "io.*"-scoped) but if it ever happens, the real label silently loses. Flagged
        // rather than guarded against, since guarding a collision this unlikely is not worth the code.
        var labels = new Dictionary<string, string>(NerdctlJson.Labels(container.Labels))
        {
            ["kontena.nerdctl.names"] = container.Names,
        };

        return new ContainerSummary
        {
            Id = container.Id,
            Name = DisplayName(container.Names, container.Id),
            Image = container.Image,
            State = StateFromPsStatus(container.Status),
            Status = container.Status,
            // "0.0.0.0:8080->80/tcp, 0.0.0.0:9090->90/udp" — a comma-separated human string, not
            // structured JSON (see Fixtures/ps-ports.json). NerdctlJson.Ports does the parsing so it
            // gets the same shared, individually tested treatment as Size/Time/Labels above.
            Ports = NerdctlJson.Ports(container.Ports),
            Labels = labels,
            CreatedAt = NerdctlJson.Time(container.CreatedAt),
            Backend = backend,
        };
    }

    /// <summary>
    /// Maps one <c>images</c> row. <see cref="NerdctlImage.Tag"/> may literally be the string
    /// <c>"&lt;none&gt;"</c>; it is passed through unchanged because
    /// <see cref="ImageSummary.Tag"/> already defaults to that exact string, so nerdctl's and the SDK's
    /// notion of "no tag" already agree.
    /// </summary>
    public static ImageSummary ToImage(this NerdctlImage image) => new()
    {
        Id = image.Id,
        Repository = image.Repository,
        Tag = image.Tag,
        // Size, not BlobSize: BlobSize is the compressed layer size, Size is the on-disk size the CEAL
        // means by SizeBytes.
        SizeBytes = NerdctlJson.Size(image.Size),
        CreatedAt = NerdctlJson.Time(image.CreatedAt),
        // Unlike Docker's image list, nerdctl's `images` row carries no per-image container count —
        // there is nothing to derive "in use" from without a second, separate call, so this is always
        // false rather than invented.
        InUse = false,
    };

    /// <summary>
    /// Maps one <c>volume ls</c> row, against the populated capture in <c>Fixtures/volume-ls.json</c>.
    /// <see cref="VolumeSummary.Driver"/> defaults to <c>"local"</c> — the same value nerdctl's real
    /// capture happens to report — so a missed mapping here would look correct while reading nothing;
    /// <see cref="VolumeSummary.Mountpoint"/> has no such coincidental default, so it is the field that
    /// actually proves this mapping runs. <see cref="NerdctlVolume.Labels"/> is left unmapped:
    /// <see cref="VolumeSummary"/> has no label map to put it in. <see cref="NerdctlVolume.Size"/> is
    /// also left unmapped — see that field's remarks.
    /// </summary>
    public static VolumeSummary ToVolume(this NerdctlVolume volume) => new()
    {
        Name = volume.Name,
        Driver = volume.Driver,
        Mountpoint = volume.Mountpoint,
    };

    /// <summary>
    /// Maps one <c>network ls</c> row. <c>kindnet</c>, <c>host</c> and <c>none</c> were all observed
    /// with an empty <see cref="NerdctlNetwork.Id"/> — three different networks sharing the same empty
    /// key — so <see cref="NetworkSummary.Id"/> is passed through as-is (possibly empty) rather than
    /// invented, and any lookup must key on <see cref="NetworkSummary.Name"/> instead.
    /// </summary>
    public static NetworkSummary ToNetwork(this NerdctlNetwork network) => new()
    {
        Id = network.Id,
        Name = network.Name,
        // nerdctl's `network ls` reports no driver at all. Leaving NetworkSummary.Driver unset would
        // fall back to the SDK record's own default, "bridge" — stating a specific, wrong brand name as
        // fact for e.g. a CNI-managed network like "kindnet" is worse than saying nothing. For the two
        // reserved names the network's own name doubles as an honest driver name (same convention
        // Docker uses for its "host" network); "bridge" gets the same treatment for consistency, since
        // that one really is its own driver name too. Anything else — no evidence, so empty rather than
        // a guess.
        Driver = network.Name is "bridge" or "host" or "none" ? network.Name : string.Empty,
        // Scope defaults to the SDK's "local" and needs no override: nerdctl networks really are local.
        // The built-in check mirrors DockerEngine.MapNetwork's exactly, for the same three reserved names.
        IsBuiltIn = network.Name is "bridge" or "host" or "none",
    };

    /// <summary>
    /// Maps <c>nerdctl inspect</c>'s single element the same way
    /// <c>Kontena.Adapters.Docker.DockerEngine.MapInspect</c> maps Docker's, field for field, with two
    /// deliberate departures where nerdctl's payload differs from what that mapping assumes:
    /// <list type="bullet">
    /// <item><description><c>Config.Cmd</c>/<c>Config.Entrypoint</c> were absent on every captured
    /// CRI container; the container's top-level <c>Path</c>/<c>Args</c> were always present and are
    /// the real invocation, so they are used whenever <c>Config</c> supplies nothing.</description></item>
    /// <item><description>The top-level <c>Name</c> was empty on every captured CRI container — see
    /// <see cref="InspectName"/>.</description></item>
    /// </list>
    /// </summary>
    public static ContainerInspect ToInspect(this NerdctlInspectContainer inspect)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in inspect.Config.Env ?? [])
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                env[entry[..eq]] = entry[(eq + 1)..];
            else
                env[entry] = string.Empty;
        }

        var command = new List<string>();
        if (inspect.Config.Entrypoint is { } entrypoint)
            command.AddRange(entrypoint);
        if (inspect.Config.Cmd is { } cmd)
            command.AddRange(cmd);
        if (command.Count == 0)
        {
            if (!string.IsNullOrEmpty(inspect.Path))
                command.Add(inspect.Path);
            command.AddRange(inspect.Args);
        }

        var mounts = inspect.Mounts
            .Select(m => new InspectMount(m.Type, m.Source, m.Destination, m.RW))
            .ToList();

        var networks = inspect.NetworkSettings.Networks
            .Select(kv => new InspectNetwork(kv.Key, kv.Value.IPAddress ?? string.Empty, kv.Value.Gateway ?? string.Empty))
            .ToList();

        return new ContainerInspect
        {
            Id = inspect.Id,
            Name = InspectName(inspect),
            Image = inspect.Config.Image,
            ImageId = inspect.Image,
            State = StateFromInspectStatus(inspect.State.Status),
            Status = inspect.State.Status,
            CreatedAt = NerdctlJson.Time(inspect.Created),
            StartedAt = ParseOptionalTime(inspect.State.StartedAt),
            FinishedAt = ParseOptionalTime(inspect.State.FinishedAt),
            ExitCode = inspect.State.ExitCode,
            Pid = inspect.State.Pid,
            // nerdctl's inspect has no OOMKilled key at all (Docker's does), so there is nothing to
            // read here — always false rather than guessed from the exit code alone (see
            // ContainerInspect.OomKilled's own remarks on why exit code 137 cannot answer this, KON-150).
            OomKilled = false,
            RestartCount = inspect.RestartCount,
            MemoryLimitBytes = inspect.HostConfig.Memory is > 0 and var memory ? memory : null,
            Error = inspect.State.Error,
            // Every CRI-managed container observed has HostConfig.RestartPolicy.Name == "" — kubelet
            // restarts these itself and never sets a Docker-style policy — which MapRestartPolicy's
            // fallback maps to RestartPolicy.No, the enum's own zero value. That states "No" as fact for
            // a container kubelet does restart, but the SDK's RestartPolicy enum has no "unknown" member
            // to say otherwise with, so there is nothing to change here (same class of gap as OomKilled
            // above).
            RestartPolicy = MapRestartPolicy(inspect.HostConfig.RestartPolicy.Name),
            Command = string.Join(" ", command),
            WorkingDirectory = inspect.Config.WorkingDir ?? string.Empty,
            User = inspect.Config.User ?? string.Empty,
            EnvironmentVariables = env,
            Labels = inspect.Config.Labels is { } labels
                ? new Dictionary<string, string>(labels)
                : new Dictionary<string, string>(),
            Mounts = mounts,
            Networks = networks,
        };
    }

    /// <summary>
    /// The last path segment of a <c>k8s://namespace/pod/container</c> name, or the name unchanged for
    /// a plain <c>nerdctl run</c> container that never had that prefix. Falls back to the short id on
    /// the (unobserved) chance <c>Names</c> is itself empty, so a list row is never blank.
    /// </summary>
    private static string DisplayName(string names, string id)
    {
        if (names.Length == 0)
            return id[..Math.Min(12, id.Length)];

        var lastSlash = names.LastIndexOf('/');
        return lastSlash >= 0 ? names[(lastSlash + 1)..] : names;
    }

    /// <summary>
    /// <see cref="NerdctlInspectContainer.Name"/> was empty on every captured CRI container — nerdctl's
    /// CRI plugin never assigns the conventional name Docker/`nerdctl run` would. The CRI's own
    /// container-name label carries the same information <c>ps</c>'s <c>Names</c> column does, so it is
    /// the first fallback; the short id is the last resort, matching <see cref="DisplayName"/>'s own.
    /// </summary>
    private static string InspectName(NerdctlInspectContainer inspect)
    {
        var name = inspect.Name.TrimStart('/');
        if (name.Length > 0)
            return name;

        return inspect.Config.Labels?.GetValueOrDefault("io.kubernetes.container.name")
            ?? inspect.Id[..Math.Min(12, inspect.Id.Length)];
    }

    /// <summary>nerdctl's own optional-timestamp convention: an empty string, not a zero date — treated the same as Docker's zero date, as "unset".</summary>
    private static DateTimeOffset? ParseOptionalTime(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var when = NerdctlJson.Time(text);
        return when == default ? null : when;
    }

    /// <summary>
    /// <c>ps</c>'s <see cref="NerdctlContainer.Status"/> is a bare word for a running, created or
    /// paused container ("Up", "Created", "Paused"), but not for a stopped one — a real capture reads
    /// "Exited (0) Less than a second ago" (see <c>Fixtures/ps-states.json</c>), Docker's own shape for
    /// that state. All branches match by prefix rather than exact string for exactly that reason: it
    /// already covers the one case observed to carry a suffix, and leaves room for a future nerdctl
    /// version to add one elsewhere.
    /// </summary>
    private static ContainerState StateFromPsStatus(string status) => status switch
    {
        _ when status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) => ContainerState.Running,
        _ when status.StartsWith("Created", StringComparison.OrdinalIgnoreCase) => ContainerState.Created,
        _ when status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase) => ContainerState.Exited,
        _ when status.StartsWith("Paused", StringComparison.OrdinalIgnoreCase) => ContainerState.Paused,
        _ when status.StartsWith("Restarting", StringComparison.OrdinalIgnoreCase) => ContainerState.Restarting,
        _ when status.StartsWith("Dead", StringComparison.OrdinalIgnoreCase) => ContainerState.Dead,
        _ when status.StartsWith("Removal", StringComparison.OrdinalIgnoreCase) => ContainerState.Removing,
        _ => ContainerState.Unknown,
    };

    /// <summary><c>inspect</c>'s <c>State.Status</c> uses Docker's own lowercase vocabulary — the same switch <c>DockerEngine.MapState</c> uses.</summary>
    private static ContainerState StateFromInspectStatus(string status) => status switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "paused" => ContainerState.Paused,
        "restarting" => ContainerState.Restarting,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        "removing" => ContainerState.Removing,
        _ => ContainerState.Unknown,
    };

    private static RestartPolicy MapRestartPolicy(string name) => name switch
    {
        "always" => RestartPolicy.Always,
        "on-failure" => RestartPolicy.OnFailure,
        "unless-stopped" => RestartPolicy.UnlessStopped,
        _ => RestartPolicy.No,
    };
}
