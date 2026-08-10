using Kontena.Core.Migration;
using Kontena.Sdk.Models;

namespace Kontena.Core.Tests.Migration;

/// <summary>
/// One test per rule the planner applies. Every case is spelled out rather than represented by one
/// of its kind: a parametrisation that leaves the dangerous cases out gives coverage you only think
/// you have (KON-344).
/// </summary>
public sealed class ContainerMigrationPlannerTests
{
    private static ContainerInspect Container(Action<ContainerInspectBuilder>? tweak = null)
    {
        var builder = new ContainerInspectBuilder();
        tweak?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>An Apple-shaped target: no compose, no restart policy, everything else present.</summary>
    private static MigrationTarget Target(Action<MigrationTargetBuilder>? tweak = null)
    {
        var builder = new MigrationTargetBuilder();
        tweak?.Invoke(builder);
        return builder.Build();
    }

    // ── Applied ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_request_carries_image_name_ports_environment_command_workdir_user_and_labels()
    {
        var source = new MigrationSource(Container(c =>
        {
            c.Image = "nginx:alpine";
            c.Name = "web";
            c.Entrypoint = ["/docker-entrypoint.sh"];
            c.Cmd = ["nginx", "-g", "daemon off;"];
            c.WorkingDirectory = "/srv";
            c.User = "999";
            c.Labels = new Dictionary<string, string> { ["role"] = "web" };
            c.EnvironmentVariables = new Dictionary<string, string> { ["TZ"] = "Europe/Amsterdam" };
        }), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target());

        Assert.Equal("nginx:alpine", plan.Request.Image);
        Assert.Equal("web", plan.Request.Name);
        Assert.Equal(["/docker-entrypoint.sh"], plan.Request.Entrypoint);
        Assert.Equal(["nginx", "-g", "daemon off;"], plan.Request.Command);
        Assert.Equal("/srv", plan.Request.WorkingDirectory);
        Assert.Equal("999", plan.Request.User);
        Assert.Equal("web", plan.Request.Labels["role"]);
        Assert.Equal("Europe/Amsterdam", plan.Request.Environment["TZ"]);
    }

    /// <summary>
    /// Ports live on the summary, not the inspect. A web server that arrives without its published
    /// port is the exact shape of failure this ticket exists to avoid.
    /// </summary>
    [Fact]
    public void Published_ports_are_carried_over()
    {
        var source = new MigrationSource(Container(), ComposeSiblings: 0)
        {
            Ports = [new PortBinding(8080, 80)],
        };

        var plan = ContainerMigrationPlanner.Plan(source, Target());

        Assert.Equal(8080, Assert.Single(plan.Request.Ports).HostPort);
    }

    /// <summary>
    /// The migrated container is handed back stopped. Starting it is the user's move — that is when
    /// they find out whether it works, and starting it for them takes that moment away.
    /// </summary>
    [Fact]
    public void The_request_never_starts_the_container()
    {
        var plan = ContainerMigrationPlanner.Plan(
            new MigrationSource(Container(), ComposeSiblings: 0), Target());

        Assert.False(plan.Request.Start);
    }

    [Fact]
    public void Bind_mounts_are_carried_over_and_not_copied()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("bind", "/srv/site", "/usr/share/nginx/html", ReadWrite: false)]),
            ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target());

        var mount = Assert.Single(plan.Request.Mounts);
        Assert.Equal(MountSpec.Bind, mount.Type);
        Assert.Equal("/srv/site", mount.Source);
        Assert.True(mount.ReadOnly);
        Assert.Empty(plan.Volumes);
    }

    // ── Dropped ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RestartPolicy.Always)]
    [InlineData(RestartPolicy.UnlessStopped)]
    [InlineData(RestartPolicy.OnFailure)]
    public void A_restart_policy_is_dropped_when_the_target_has_none(RestartPolicy policy)
    {
        var source = new MigrationSource(Container(c => c.RestartPolicy = policy), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsRestartPolicy = false));

        Assert.Contains(plan.Notes, n =>
            n.Kind is MigrationNoteKind.Dropped && n.Subject == "Restart policy");
        Assert.Equal(RestartPolicy.No, plan.Request.RestartPolicy);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void A_restart_policy_is_kept_when_the_target_has_one()
    {
        var source = new MigrationSource(
            Container(c => c.RestartPolicy = RestartPolicy.Always), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsRestartPolicy = true));

        Assert.Equal(RestartPolicy.Always, plan.Request.RestartPolicy);
        Assert.DoesNotContain(plan.Notes, n => n.Subject == "Restart policy");
    }

    /// <summary>
    /// <c>--network</c> is a create-time flag on the engines that lack <c>network connect</c>, so only
    /// the first attachment can be honoured. Saying which ones were lost matters more than which one
    /// won.
    /// </summary>
    [Fact]
    public void Every_network_beyond_the_first_is_dropped()
    {
        var source = new MigrationSource(Container(c => c.Networks =
        [
            new InspectNetwork("frontend", "10.0.0.2", "10.0.0.1"),
            new InspectNetwork("backend", "10.0.1.2", "10.0.1.1"),
        ]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target());

        Assert.Equal("frontend", plan.Request.Network);
        Assert.Contains(plan.Notes, n =>
            n.Kind is MigrationNoteKind.Dropped && n.Detail.Contains("backend", StringComparison.Ordinal));
    }

    /// <summary>
    /// Always said, never conditional on labels: two plain containers that address each other by name
    /// are undetectable, so the only honest place for this is a line every migration reads.
    /// </summary>
    [Fact]
    public void Name_resolution_is_reported_as_dropped_whenever_the_target_has_no_compose()
    {
        var plan = ContainerMigrationPlanner.Plan(
            new MigrationSource(Container(), ComposeSiblings: 0),
            Target(t => t.SupportsCompose = false));

        Assert.Contains(plan.Notes, n =>
            n.Kind is MigrationNoteKind.Dropped && n.Subject == "Name resolution");
    }

    /// <summary>
    /// The dialog must not read as a complete transfer. What Kontena does not inspect cannot be
    /// migrated, and that has to be said rather than left to be discovered.
    /// </summary>
    [Fact]
    public void What_the_inspect_does_not_carry_is_reported_as_dropped()
    {
        var plan = ContainerMigrationPlanner.Plan(
            new MigrationSource(Container(), ComposeSiblings: 0), Target());

        Assert.Contains(plan.Notes, n =>
            n.Kind is MigrationNoteKind.Dropped
            && n.Detail.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    // ── Blocked ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_container_with_compose_siblings_blocks_when_the_target_has_no_compose()
    {
        var source = new MigrationSource(Container(c => c.Labels =
            new Dictionary<string, string> { ["com.docker.compose.project"] = "shop" }), ComposeSiblings: 2);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsCompose = false));

        Assert.Contains(plan.Notes, n => n.Kind is MigrationNoteKind.Blocked);
        Assert.False(plan.CanRun);
    }

    /// <summary>The last survivor of an old project is a plain container, and must migrate.</summary>
    [Fact]
    public void A_compose_label_without_siblings_does_not_block()
    {
        var source = new MigrationSource(Container(c => c.Labels =
            new Dictionary<string, string> { ["com.docker.compose.project"] = "shop" }), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsCompose = false));

        Assert.True(plan.CanRun);
    }

    [Fact]
    public void Compose_siblings_do_not_block_a_target_that_does_compose()
    {
        var source = new MigrationSource(Container(c => c.Labels =
            new Dictionary<string, string> { ["com.docker.compose.project"] = "shop" }), ComposeSiblings: 2);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsCompose = true));

        Assert.True(plan.CanRun);
    }

    [Fact]
    public void A_name_already_in_use_on_the_target_blocks()
    {
        var source = new MigrationSource(Container(c => c.Name = "web"), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.ContainerNames = ["web"]));

        Assert.Contains(plan.Notes, n =>
            n.Kind is MigrationNoteKind.Blocked && n.Subject == "Name");
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void A_target_that_cannot_transfer_volumes_blocks_a_container_that_has_one()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("volume", "data", "/data", ReadWrite: true)]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsVolumeTransfer = false));

        Assert.False(plan.CanRun);
    }

    /// <summary>
    /// Bind mounts are re-attached, never copied, so an engine that cannot transfer volume contents
    /// has nothing to stop it here — blocking on one would refuse a migration that works.
    /// </summary>
    [Fact]
    public void A_target_that_cannot_transfer_volumes_still_takes_a_bind_only_container()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("bind", "/srv/site", "/site", ReadWrite: true)]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.SupportsVolumeTransfer = false));

        Assert.True(plan.CanRun);
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_volume_that_does_not_exist_on_the_target_is_copied()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("volume", "data", "/data", ReadWrite: true)]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target());

        var volume = Assert.Single(plan.Volumes);
        Assert.Equal("data", volume.Name);
        Assert.False(volume.ExistsOnTarget);
        Assert.True(volume.WillCopy);
    }

    [Fact]
    public void An_existing_empty_volume_on_the_target_is_still_copied_into()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("volume", "data", "/data", ReadWrite: true)]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(
            source, Target(t => t.Volumes = new Dictionary<string, bool> { ["data"] = false }));

        Assert.True(Assert.Single(plan.Volumes).WillCopy);
    }

    /// <summary>
    /// A name that matches is not permission to overwrite someone's data. Skipping is the default and
    /// the note says so, so the choice is visible instead of implied.
    /// </summary>
    [Fact]
    public void An_existing_volume_with_data_is_skipped_unless_overwrite_is_set()
    {
        var source = new MigrationSource(Container(c =>
            c.Mounts = [new InspectMount("volume", "data", "/data", ReadWrite: true)]), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(
            source, Target(t => t.Volumes = new Dictionary<string, bool> { ["data"] = true }));

        var volume = Assert.Single(plan.Volumes);
        Assert.False(volume.WillCopy);
        Assert.True((volume with { Overwrite = true }).WillCopy);
    }

    [Fact]
    public void An_image_that_is_not_on_the_target_is_reported_as_a_pull()
    {
        var plan = ContainerMigrationPlanner.Plan(
            new MigrationSource(Container(c => c.Image = "nginx:alpine"), ComposeSiblings: 0),
            Target(t => t.HasImage = false));

        Assert.Contains(plan.Notes, n =>
            n.Subject == "Image" && n.Detail.Contains("pull", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Blocked lines come first: they are what decides whether there is a run button.</summary>
    [Fact]
    public void Blocked_notes_are_listed_before_dropped_and_applied_ones()
    {
        var source = new MigrationSource(Container(c => c.Name = "web"), ComposeSiblings: 0);

        var plan = ContainerMigrationPlanner.Plan(source, Target(t => t.ContainerNames = ["web"]));

        Assert.Equal(MigrationNoteKind.Blocked, plan.Notes[0].Kind);
        Assert.Equal(
            plan.Notes.Select(n => n.Kind).OrderByDescending(k => k),
            plan.Notes.Select(n => n.Kind));
    }
}

/// <summary>A container with ordinary defaults, so each test only spells out what it is about.</summary>
internal sealed class ContainerInspectBuilder
{
    public string Image { get; set; } = "alpine:3.20";
    public string Name { get; set; } = "app";
    public IReadOnlyList<string> Entrypoint { get; set; } = [];
    public IReadOnlyList<string> Cmd { get; set; } = [];
    public string WorkingDirectory { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.No;
    public IReadOnlyDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; set; } =
        new Dictionary<string, string>();

    public IReadOnlyList<InspectMount> Mounts { get; set; } = [];
    public IReadOnlyList<InspectNetwork> Networks { get; set; } = [];

    public ContainerInspect Build() => new()
    {
        Id = "abc123",
        Name = Name,
        Image = Image,
        ImageId = "sha256:abc",
        State = ContainerState.Running,
        Entrypoint = Entrypoint,
        Cmd = Cmd,
        WorkingDirectory = WorkingDirectory,
        User = User,
        RestartPolicy = RestartPolicy,
        Labels = Labels,
        EnvironmentVariables = EnvironmentVariables,
        Mounts = Mounts,
        Networks = Networks,
    };
}

/// <summary>An Apple-shaped target by default: no compose, no restart policy, image already there.</summary>
internal sealed class MigrationTargetBuilder
{
    public bool SupportsCompose { get; set; }
    public bool SupportsRestartPolicy { get; set; }
    public bool SupportsVolumeTransfer { get; set; } = true;
    public bool HasImage { get; set; } = true;
    public IReadOnlyCollection<string> ContainerNames { get; set; } = [];

    public IReadOnlyDictionary<string, bool> Volumes { get; set; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    public MigrationTarget Build() => new()
    {
        Capabilities = new EngineCapabilities
        {
            SupportsCompose = SupportsCompose,
            SupportsRestartPolicy = SupportsRestartPolicy,
            SupportsVolumeTransfer = SupportsVolumeTransfer,
        },
        ContainerNames = ContainerNames,
        Volumes = Volumes,
        HasImage = HasImage,
    };
}
