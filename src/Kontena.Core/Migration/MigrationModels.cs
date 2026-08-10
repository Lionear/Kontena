using Kontena.Sdk.Models;

namespace Kontena.Core.Migration;

/// <summary>What a note says about one aspect of a migration.</summary>
public enum MigrationNoteKind
{
    /// <summary>Carried over as-is.</summary>
    Applied,

    /// <summary>Cannot be carried over; the migration still runs.</summary>
    Dropped,

    /// <summary>Cannot be carried over, and the migration must not run.</summary>
    Blocked,
}

/// <summary>One line in the plan the user confirms.</summary>
/// <param name="Kind">Applied, dropped, or blocked.</param>
/// <param name="Subject">What it is about, e.g. "Restart policy" or a volume name.</param>
/// <param name="Detail">Why, in the user's words.</param>
public sealed record MigrationNote(MigrationNoteKind Kind, string Subject, string Detail);

/// <summary>One named volume and what the migration intends to do with it.</summary>
/// <param name="Name">Volume name, the same on both sides.</param>
/// <param name="ExistsOnTarget">True when the target engine already has a volume by this name.</param>
/// <param name="TargetHasData">True when that existing volume is not empty.</param>
public sealed record VolumePlan(string Name, bool ExistsOnTarget, bool TargetHasData)
{
    /// <summary>
    /// Set by the user to copy over a volume that already holds data. False by default: overwriting
    /// someone's data because a name matched is not a thing to do without being asked.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>Whether this volume's contents will actually be copied.</summary>
    public bool WillCopy => !ExistsOnTarget || !TargetHasData || Overwrite;
}

/// <summary>Everything the planner needs to know about the container being migrated.</summary>
/// <param name="Container">The source container's full inspect.</param>
/// <param name="ComposeSiblings">
/// How many *other* containers on the source engine share this one's
/// <c>com.docker.compose.project</c> label. Zero for a container that is not part of a project, and
/// zero for the last survivor of an old one — which is why it is a count and not a flag.
/// </param>
public sealed record MigrationSource(ContainerInspect Container, int ComposeSiblings)
{
    /// <summary>
    /// The container's published ports, which live on <see cref="ContainerSummary"/> rather than on
    /// the inspect — so the caller reads them off the list entry for the same container.
    /// </summary>
    public IReadOnlyList<PortBinding> Ports { get; init; } = [];
}

/// <summary>Everything the planner needs to know about where it is going.</summary>
public sealed record MigrationTarget
{
    /// <summary>What the target engine can do.</summary>
    public required EngineCapabilities Capabilities { get; init; }

    /// <summary>Container names already taken on the target.</summary>
    public IReadOnlyCollection<string> ContainerNames { get; init; } = [];

    /// <summary>Volume name → true when it already holds data.</summary>
    public IReadOnlyDictionary<string, bool> Volumes { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>True when the image is already present on the target and needs no pull.</summary>
    public bool HasImage { get; init; }
}

/// <summary>The plan a user confirms, and the runner executes.</summary>
public sealed record MigrationPlan
{
    /// <summary>What will be created on the target.</summary>
    public required CreateContainerRequest Request { get; init; }

    /// <summary>Applied, dropped and blocked lines, in that order of severity.</summary>
    public required IReadOnlyList<MigrationNote> Notes { get; init; }

    /// <summary>The named volumes involved, in the order they appear on the container.</summary>
    public required IReadOnlyList<VolumePlan> Volumes { get; init; }

    /// <summary>False when any note blocks; the dialog has no run button then.</summary>
    public bool CanRun => !Notes.Any(n => n.Kind is MigrationNoteKind.Blocked);
}
