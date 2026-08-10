using Kontena.Sdk.Models;

namespace Kontena.Core.Migration;

/// <summary>
/// Turns a container's inspect into a request that recreates it on another engine, plus the honest
/// list of what will not come along.
/// <para>
/// Pure: it reads what it is handed and returns a plan. Every I/O call — inspecting, listing, probing
/// the target — happens in the caller, which is what makes every rule below testable with fixed
/// values instead of a live engine.
/// </para>
/// <para>
/// It knows nothing about Apple <c>container</c>. Every rule hangs on an
/// <see cref="EngineCapabilities"/> flag, so Docker→Podman and Docker→nerdctl come out of the same
/// code.
/// </para>
/// </summary>
public static class ContainerMigrationPlanner
{
    /// <summary>The label Compose puts on every container it creates.</summary>
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>
    /// What a container can carry that <see cref="ContainerInspect"/> does not read. Listed by name
    /// rather than summarised, because "some settings may not transfer" is the kind of sentence that
    /// reads as a formality.
    /// </summary>
    private const string NotInspected =
        "Health check, capabilities, devices, ulimits and a read-only root filesystem are not read by "
        + "Kontena, so they cannot be migrated. Check them by hand if this container relies on them.";

    /// <summary>Builds the plan for moving <paramref name="source"/> onto <paramref name="target"/>.</summary>
    public static MigrationPlan Plan(MigrationSource source, MigrationTarget target)
    {
        var container = source.Container;
        var notes = new List<MigrationNote>();
        var mounts = new List<MountSpec>();
        var volumes = new List<VolumePlan>();

        foreach (var mount in container.Mounts)
        {
            var isVolume = string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase);

            mounts.Add(new MountSpec(
                isVolume ? MountSpec.Volume : MountSpec.Bind,
                mount.Source,
                mount.Destination,
                ReadOnly: !mount.ReadWrite));

            if (!isVolume)
                continue;

            var exists = target.Volumes.TryGetValue(mount.Source, out var hasData);
            volumes.Add(new VolumePlan(mount.Source, exists, exists && hasData));
        }

        // ── Blocked ─────────────────────────────────────────────────────────

        if (target.ContainerNames.Contains(container.Name, StringComparer.Ordinal))
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Blocked, "Name",
                $"The target engine already has a container called '{container.Name}'. "
                + "Give this one another name, or remove that one first."));
        }

        // A project's services find each other by name. Without name resolution the stack starts and
        // then fails on its first connection to a sibling — the same wall the compose runner hit.
        if (!target.Capabilities.SupportsCompose
            && source.ComposeSiblings > 0
            && container.Labels.ContainsKey(ComposeProjectLabel))
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Blocked, "Compose project",
                $"This container is one of {source.ComposeSiblings + 1} services in a Compose project, "
                + "and the target engine has no name resolution between containers. It would start and "
                + "then fail on its first connection to another service."));
        }

        if (volumes.Count > 0 && !target.Capabilities.SupportsVolumeTransfer)
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Blocked, "Volumes",
                "The target engine cannot copy volume contents, and this container has "
                + $"{volumes.Count} named volume(s). Migrating would give you empty ones."));
        }

        // ── Dropped ─────────────────────────────────────────────────────────

        var restartPolicy = container.RestartPolicy;

        if (restartPolicy is not RestartPolicy.No && !target.Capabilities.SupportsRestartPolicy)
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Dropped, "Restart policy",
                $"'{restartPolicy}' is dropped: the target engine has no restart policy at all. "
                + "The container will not come back on its own after a crash or a reboot."));

            restartPolicy = RestartPolicy.No;
        }

        if (container.Networks.Count > 1)
        {
            var dropped = container.Networks.Skip(1).Select(n => n.Name);

            notes.Add(new MigrationNote(MigrationNoteKind.Dropped, "Networks",
                $"Only '{container.Networks[0].Name}' comes along; "
                + $"{string.Join(", ", dropped)} cannot be attached after creation on this engine."));
        }

        if (!target.Capabilities.SupportsCompose)
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Dropped, "Name resolution",
                "Containers on the target engine cannot reach each other by name. Anything this "
                + "container addresses by container name will not resolve."));
        }

        notes.Add(new MigrationNote(MigrationNoteKind.Dropped, "Not inspected", NotInspected));

        // ── Applied ─────────────────────────────────────────────────────────

        notes.Add(new MigrationNote(MigrationNoteKind.Applied, "Image",
            target.HasImage
                ? $"'{container.Image}' is already on the target engine."
                : $"'{container.Image}' will be pulled onto the target engine."));

        foreach (var volume in volumes)
        {
            notes.Add(new MigrationNote(MigrationNoteKind.Applied, $"Volume '{volume.Name}'",
                volume.WillCopy
                    ? "Its contents will be copied."
                    : "It already exists on the target and holds data, so it is left alone. "
                      + "Tick it to overwrite."));
        }

        var request = new CreateContainerRequest
        {
            Image = container.Image,
            Name = container.Name,
            Entrypoint = container.Entrypoint,
            Command = container.Cmd,
            WorkingDirectory = container.WorkingDirectory is { Length: > 0 } directory ? directory : null,
            User = container.User is { Length: > 0 } user ? user : null,
            Environment = container.EnvironmentVariables,
            Labels = container.Labels,
            Mounts = mounts,

            // Ports are on the summary, not the inspect, so the caller reads them off the list entry
            // for this same container — without them a web server arrives unpublished.
            Ports = source.Ports,
            Network = container.Networks.Count > 0 ? container.Networks[0].Name : null,
            RestartPolicy = restartPolicy,

            // Handed back stopped on purpose: starting it is the moment the user finds out whether
            // the migration worked, and doing it for them takes that moment away.
            Start = false,
        };

        return new MigrationPlan
        {
            Request = request,
            Notes = [.. notes.OrderBy(n => n.Kind switch
            {
                MigrationNoteKind.Blocked => 0,
                MigrationNoteKind.Dropped => 1,
                _ => 2,
            })],
            Volumes = volumes,
        };
    }
}
