using System.Globalization;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// Turns what the <c>container</c> CLI printed into the engine-neutral models. Kept separate from
/// <see cref="AppleEngine"/> so it can be tested against captured output without a process, a macOS 26
/// host, or a running apiserver.
/// </summary>
internal static class AppleMap
{
    /// <summary>
    /// <c>container</c> uses the name as the id — there is no hash and no separate name field — so both
    /// neutral properties carry the same value. That is not a shortcut: what a user types is what the
    /// CLI reports back.
    /// </summary>
    public static ContainerSummary Container(AppleContainer source, string backend)
    {
        var configuration = source.Configuration;

        return new ContainerSummary
        {
            Id = source.Id,
            Name = source.Id,
            Image = configuration?.Image?.Reference ?? string.Empty,
            State = State(source.Status?.State),

            // No engine-supplied sentence like Docker's "Up 2 hours" exists here, so the raw state word
            // is the honest answer rather than one composed to look like Docker's.
            Status = source.Status?.State ?? string.Empty,
            Ports = [.. (configuration?.PublishedPorts ?? []).Select(Port)],
            Labels = configuration?.Labels ?? new Dictionary<string, string>(),
            CreatedAt = configuration?.CreationDate ?? default,
            Backend = backend,
        };
    }

    private static PortBinding Port(ApplePublishedPort source) =>
        new(source.HostPort, source.ContainerPort, source.Proto);

    /// <summary>
    /// Maps the same record onto the detail model. <c>container inspect</c> prints exactly what
    /// <c>container list</c> prints, so this reads more fields off one payload rather than a second,
    /// richer one — which is why there is no separate inspect DTO here the way Docker's adapter needs.
    /// <para>
    /// Four fields the Inspect tab shows for Docker have no counterpart in this CLI's output and are
    /// left at their defaults rather than filled with a plausible number: exit code, pid, restart count
    /// and OOM-killed. Each container is its own VM here; those are properties of a process on a shared
    /// kernel.
    /// </para>
    /// </summary>
    public static ContainerInspect Inspect(AppleContainer source)
    {
        var configuration = source.Configuration;
        var process = configuration?.InitProcess;

        return new ContainerInspect
        {
            Id = source.Id,
            Name = source.Id,
            Image = configuration?.Image?.Reference ?? string.Empty,
            ImageId = configuration?.Image?.Descriptor?.Digest ?? string.Empty,
            State = State(source.Status?.State),
            Status = source.Status?.State ?? string.Empty,
            CreatedAt = configuration?.CreationDate ?? default,
            StartedAt = source.Status?.StartedDate,
            MemoryLimitBytes = configuration?.Resources?.MemoryInBytes,
            Command = Command(process),

            // `Command` above is the joined display line; these two are what re-running this
            // container needs — see ContainerInspect.Entrypoint.
            Entrypoint = process?.Executable is { Length: > 0 } executable ? [executable] : [],
            Cmd = process?.Arguments is { } processArguments ? [.. processArguments] : [],

            WorkingDirectory = process?.WorkingDirectory ?? string.Empty,

            // The CLI reports the numeric uid, never a name, so the field carries the number as text
            // rather than an invented "root" for 0 — the container's own /etc/passwd is what decides
            // that, and this adapter has not read it.
            User = process?.User?.Id is { } id ? id.Uid.ToString(CultureInfo.InvariantCulture) : string.Empty,
            EnvironmentVariables = Environment(process?.Environment),
            Labels = configuration?.Labels ?? new Dictionary<string, string>(),
            Mounts = [.. (configuration?.Mounts ?? []).Select(Mount)],
            Networks = [.. (source.Status?.Networks ?? []).Select(Network)],
        };
    }

    private static string Command(AppleInitProcess? process) =>
        process is null
            ? string.Empty
            : string.Join(' ', new[] { process.Executable }.Concat(process.Arguments ?? []));

    /// <summary>
    /// Splits the OCI <c>KEY=value</c> strings into a map. Split on the first '=' only, so a value that
    /// itself contains one survives intact.
    /// </summary>
    private static Dictionary<string, string> Environment(List<string>? entries)
    {
        var variables = new Dictionary<string, string>();

        foreach (var entry in entries ?? [])
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
                variables[entry[..separator]] = entry[(separator + 1)..];
        }

        return variables;
    }

    /// <summary>
    /// Maps a mount. A named volume reports the volume's name as its source rather than the path of the
    /// disk image backing it: the path is an implementation detail of where Apple keeps volumes, and it
    /// is the name that matches what the Volumes page lists.
    /// </summary>
    private static InspectMount Mount(AppleMount source) =>
        new(
            source.VolumeName is null ? "bind" : "volume",
            source.VolumeName ?? source.Source,
            source.Destination,

            // Read-only mounts have not been observed in any capture, and reporting one as read-only on
            // a guess would be worse than reporting them all as writable, which is what `container run`
            // gives you by default.
            ReadWrite: true);

    private static InspectNetwork Network(AppleNetworkAttachment source) =>
        new(source.Network, Address(source.Ipv4Address), source.Ipv4Gateway);

    /// <summary>
    /// Drops the prefix length from a CIDR address. The neutral model wants an address, and
    /// "192.168.64.2/24" in an IP column reads as a subnet.
    /// </summary>
    private static string Address(string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? cidr : cidr[..slash];
    }

    /// <summary>
    /// Maps the lifecycle word onto the neutral enum. Only <c>running</c> and <c>stopped</c> have been
    /// observed; anything else is reported as <see cref="ContainerState.Unknown"/> rather than guessed
    /// into a neighbouring state, because a wrong dot colour is a lie and an unknown one is not.
    /// </summary>
    public static ContainerState State(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => ContainerState.Running,

        // The neutral enum's word for "created but not running" is Exited; `container` has no separate
        // never-started state, so a stopped container is the same row either way.
        "stopped" => ContainerState.Exited,
        "created" => ContainerState.Created,
        _ => ContainerState.Unknown,
    };

    /// <summary>
    /// Maps one image. The size is not on the image: it lives per platform variant, and the variants of
    /// a multi-arch index include attestation entries that are not images at all. The variant matching
    /// this host is the one that would actually run, so that is the size reported; when none matches —
    /// an image pulled for another architecture — the largest real variant is used rather than zero,
    /// which would read as "empty image" instead of "not for this machine".
    /// </summary>
    public static ImageSummary Image(AppleImage source, IReadOnlySet<string> imagesInUse)
    {
        var reference = source.Configuration?.Name ?? string.Empty;
        var (repository, tag) = SplitReference(reference);
        var variants = (source.Variants ?? []).Where(v => v.IsRealPlatform).ToList();

        var native = variants.FirstOrDefault(v =>
            string.Equals(v.Platform!.Architecture, ToolPlatform.Architecture, StringComparison.OrdinalIgnoreCase));

        return new ImageSummary
        {
            Id = source.Id,
            Repository = repository,
            Tag = tag,
            SizeBytes = native?.Size ?? variants.Select(v => v.Size).DefaultIfEmpty(0).Max(),
            CreatedAt = source.Configuration?.CreationDate ?? default,
            InUse = imagesInUse.Contains(reference),
        };
    }

    /// <summary>
    /// Splits a full reference into repository and tag. The tag is separated by the last colon, but only
    /// when that colon comes after the last slash — <c>localhost:5000/app</c> is a registry port, not a
    /// tag, and treating it as one would file every image from a private registry under the wrong name.
    /// A digest reference (<c>repo@sha256:…</c>) has no tag at all.
    /// </summary>
    public static (string Repository, string Tag) SplitReference(string reference)
    {
        if (string.IsNullOrEmpty(reference))
            return (string.Empty, "<none>");

        var digest = reference.IndexOf('@', StringComparison.Ordinal);
        if (digest >= 0)
            return (reference[..digest], "<none>");

        var colon = reference.LastIndexOf(':');
        var slash = reference.LastIndexOf('/');

        return colon > slash
            ? (reference[..colon], reference[(colon + 1)..])
            : (reference, "<none>");
    }

    /// <summary>
    /// Maps one volume. <c>sizeInBytes</c> is deliberately not carried over: it is the size the sparse
    /// disk image may grow to (512 GiB on a freshly created volume), not what is in it, and a "size"
    /// column showing that would be worse than an empty one.
    /// </summary>
    public static VolumeSummary Volume(AppleVolume source, IReadOnlyList<AppleContainer> containers)
    {
        var name = source.Configuration?.Name ?? source.Id;

        return new VolumeSummary
        {
            Name = name,
            Driver = source.Configuration?.Driver ?? "local",
            Mountpoint = source.Configuration?.Source ?? string.Empty,
            SizeBytes = null,
            UsedBy = [.. containers
                .Where(c => (c.Configuration?.Mounts ?? [])
                    .Any(m => string.Equals(m.VolumeName, name, StringComparison.Ordinal)))
                .Select(c => c.Id)],
        };
    }

    public static NetworkSummary Network(AppleNetwork source, IReadOnlyList<AppleContainer> containers)
    {
        var configuration = source.Configuration;
        var name = configuration?.Name ?? source.Id;

        return new NetworkSummary
        {
            Id = source.Id,
            Name = name,

            // `mode` is what this CLI calls the driver ("nat"). Reporting it as-is keeps the column
            // truthful; mapping it onto Docker's vocabulary ("bridge") would claim a compatibility that
            // is not there.
            Driver = string.IsNullOrEmpty(configuration?.Mode) ? "nat" : configuration.Mode,
            Scope = "local",
            Subnet = source.Status?.Ipv4Subnet,
            AttachedContainers = [.. containers
                .Where(c => (c.Configuration?.Networks ?? [])
                    .Any(n => string.Equals(n.Network, source.Id, StringComparison.Ordinal)))
                .Select(c => c.Id)],
            IsBuiltIn = configuration?.IsBuiltIn ?? false,
        };
    }

    /// <summary>
    /// Reads one line of the volume listing: <c>type|size|mtime|path</c>, as <c>stat -c</c> printed it
    /// inside the throwaway container. Returns null for anything that does not parse, and for the
    /// <c>lost+found</c> the filesystem itself put at the root.
    /// </summary>
    /// <param name="line">One <c>stat</c> line.</param>
    /// <param name="root">The directory that was listed, so the entry can be reduced to its own name.</param>
    public static VolumeEntry? VolumeEntry(string line, string root)
    {
        var parts = line.Split('|', 4);
        if (parts.Length < 4)
            return null;

        var path = parts[3].Trim();
        var name = path.StartsWith(root, StringComparison.Ordinal)
            ? path[root.Length..].TrimStart('/')
            : path;

        if (name.Length == 0)
            return null;

        // Volumes here are ext4 images, and every ext4 filesystem has a lost+found at its root that
        // nobody put there. Only at the root: a directory a user made deeper down keeps its name.
        if (name == "lost+found" && root == "/kontena-volume")
            return null;

        _ = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        _ = long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds);

        return new VolumeEntry(
            name,

            // `stat -c %F` spells it out: "directory", "regular file", "symbolic link". A link is not a
            // directory here even when it points at one — following it would leave the mount.
            parts[0].Trim() == "directory",
            size,
            seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
    }

    /// <summary>
    /// Reads what an image was built with, from the variant that would actually run here — the same
    /// native-first choice <see cref="Image"/> makes for the size.
    /// <para>
    /// Only the environment comes back. This CLI's image config carries no <c>ExposedPorts</c> and no
    /// <c>Volumes</c> key at all (captured against <c>nginx:alpine</c>, which declares both), so those
    /// stay empty because the source is silent — not because the image is.
    /// </para>
    /// </summary>
    public static ImageConfig ImageConfig(AppleImage source)
    {
        var variants = (source.Variants ?? []).Where(v => v.IsRealPlatform).ToList();

        var native = variants.FirstOrDefault(v =>
            string.Equals(v.Platform!.Architecture, ToolPlatform.Architecture, StringComparison.OrdinalIgnoreCase));

        return new ImageConfig
        {
            Environment = Environment((native ?? variants.FirstOrDefault())?.Config?.Config?.Env),
        };
    }

    /// <summary>
    /// Maps one stats sample. Everything but the CPU figure is a straight copy — this CLI reports bytes
    /// as bytes.
    /// <para>
    /// The CPU percentage is the rise in the container's cumulative CPU time over the wall-clock time
    /// between the two samples, on the same scale Docker uses: 100% is one core saturated, and a
    /// container using two cores flat out reads as 200%. With no previous sample, or a non-positive
    /// interval, it is zero rather than a guess.
    /// </para>
    /// </summary>
    public static ContainerStats Stats(
        AppleStats current, AppleStats? previous, TimeSpan elapsed, string containerId) =>
        new()
        {
            ContainerId = containerId,
            CpuPercent = CpuPercent(current, previous, elapsed),
            MemoryUsedBytes = current.MemoryUsageBytes,
            MemoryLimitBytes = current.MemoryLimitBytes,
            NetRxBytes = current.NetworkRxBytes,
            NetTxBytes = current.NetworkTxBytes,
            BlockReadBytes = current.BlockReadBytes,
            BlockWriteBytes = current.BlockWriteBytes,
        };

    private static double CpuPercent(AppleStats current, AppleStats? previous, TimeSpan elapsed)
    {
        if (previous is null || elapsed <= TimeSpan.Zero)
            return 0;

        // A counter that went backwards means the container was restarted between samples, so the two
        // are not comparable. Zero says "no reading" instead of a negative percentage.
        var consumed = current.CpuUsageUsec - previous.CpuUsageUsec;

        return consumed <= 0 ? 0 : consumed / elapsed.TotalMicroseconds * 100;
    }

    /// <summary>
    /// Picks the CLI's own version out of what <c>system version</c> prints. The apiserver's entry is
    /// listed alongside it and its <c>version</c> field holds a whole sentence
    /// ("container-apiserver version 1.2.2 (build: release, commit: 0190097)"), so taking the first
    /// entry blindly would put that sentence in the title bar.
    /// </summary>
    public static string Version(IReadOnlyList<AppleVersion> entries) =>
        entries.FirstOrDefault(e =>
            string.Equals(e.AppName, AppleVersion.CliAppName, StringComparison.Ordinal))?.Version
        ?? string.Empty;
}
