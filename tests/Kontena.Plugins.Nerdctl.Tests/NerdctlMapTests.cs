using System.Text.Json;
using Kontena.Sdk.Models;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// Tests the mapping from nerdctl's own JSON shapes onto the CEAL, against the fixtures captured from
/// a real nerdctl 2.3.5 (Notes/nerdctl-cli-formats.md) — never against hand-written JSON, so a test
/// passing here means the mapping survives what nerdctl actually prints.
/// </summary>
public sealed class NerdctlMapTests
{
    private const string Backend = "nerdctl";

    private static readonly string[] ExpectedNetworkNames = ["kindnet", "host", "none"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static IReadOnlyList<NerdctlContainer> Containers() =>
        NerdctlJson.Parse<NerdctlContainer>(Fixture("ps.json"));

    private static IReadOnlyList<NerdctlImage> Images() =>
        NerdctlJson.Parse<NerdctlImage>(Fixture("images.json"));

    private static IReadOnlyList<NerdctlNetwork> Networks() =>
        NerdctlJson.Parse<NerdctlNetwork>(Fixture("network-ls.json"));

    private static NerdctlInspectContainer Inspect() =>
        JsonSerializer.Deserialize<NerdctlInspectContainer[]>(Fixture("inspect.json"), Options)![0];

    private static NerdctlContainer PortsRow() =>
        Assert.Single(NerdctlJson.Parse<NerdctlContainer>(Fixture("ps-ports.json")));

    private static IReadOnlyList<NerdctlContainer> StateRows() =>
        NerdctlJson.Parse<NerdctlContainer>(Fixture("ps-states.json"));

    // ── ps.json → ContainerSummary ─────────────────────────────────────────────────────────────

    [Fact]
    public void Maps_id_image_and_state_from_status()
    {
        var summary = Assert.Single(Containers(), c => c.Id == "281c109b7ece").ToSummary(Backend);

        Assert.Equal("281c109b7ece", summary.Id);
        Assert.Equal("docker.io/kindest/local-path-provisioner:v20260521-9fb22683", summary.Image);
        Assert.Equal(ContainerState.Running, summary.State); // Status "Up"
        Assert.Equal(Backend, summary.Backend);
    }

    [Fact]
    public void Created_status_maps_to_created_state()
    {
        var summary = Assert.Single(Containers(), c => c.Id == "841530983c81").ToSummary(Backend);

        Assert.Equal(ContainerState.Created, summary.State);
        Assert.Equal("Created", summary.Status);
    }

    [Fact]
    public void Labels_come_from_the_comma_joined_string()
    {
        var summary = Assert.Single(Containers(), c => c.Id == "281c109b7ece").ToSummary(Backend);

        Assert.Equal("container", summary.Labels["io.cri-containerd.kind"]);
        Assert.Equal("local-path-provisioner", summary.Labels["io.kubernetes.container.name"]);
    }

    [Fact]
    public void Cri_names_show_their_last_segment_as_the_display_name_and_keep_the_full_string()
    {
        // Names is "k8s://local-path-storage/local-path-provisioner-855c7b7774-vw7t9/local-path-provisioner" —
        // not a name any user recognises. The container name (last segment) is what a list should show.
        var summary = Assert.Single(Containers(), c => c.Id == "281c109b7ece").ToSummary(Backend);

        Assert.Equal("local-path-provisioner", summary.Name);
        Assert.Equal(
            "k8s://local-path-storage/local-path-provisioner-855c7b7774-vw7t9/local-path-provisioner",
            summary.Labels["kontena.nerdctl.names"]);
    }

    [Fact]
    public void Ps_command_is_never_used_for_the_real_command_line()
    {
        // ps's Command is quoted and ellipsis-truncated ("\"local-path-provisio…\""); inspect's is not.
        var truncated = Assert.Single(Containers(), c => c.Id == "281c109b7ece").Command;
        var real = Inspect().ToInspect().Command;

        Assert.Contains('…', truncated);
        Assert.DoesNotContain('…', real);
        Assert.StartsWith("local-path-provisioner --debug start", real, StringComparison.Ordinal);
    }

    // ── ps-ports.json / ps-states.json → ContainerSummary ──────────────────────────────────────

    [Fact]
    public void Published_ports_are_parsed_from_the_comma_separated_string()
    {
        // Real capture: a container started with `-p 8080:80 -p 9090:90/udp`. This is the case that
        // actually carries weight — an empty Ports list is ContainerSummary.Ports' own default, so a
        // test only ever exercising the empty case would pass whether or not parsing ran at all.
        var summary = PortsRow().ToSummary(Backend);

        Assert.Equal(
            [new PortBinding(8080, 80, "tcp"), new PortBinding(9090, 90, "udp")],
            summary.Ports);
    }

    [Fact]
    public void No_published_ports_is_an_empty_list()
    {
        var summary = Assert.Single(Containers(), c => c.Id == "281c109b7ece").ToSummary(Backend);

        Assert.Empty(summary.Ports);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa", ContainerState.Running)] // Status "Up"
    [InlineData("bbbbbbbbbbbb", ContainerState.Exited)] // Status "Exited (0) Less than a second ago"
    [InlineData("cccccccccccc", ContainerState.Paused)] // Status "Paused"
    [InlineData("dddddddddddd", ContainerState.Created)] // Status "Created"
    public void Each_observed_ps_status_maps_to_its_state(string id, ContainerState expected)
    {
        var summary = Assert.Single(StateRows(), c => c.Id == id).ToSummary(Backend);

        Assert.Equal(expected, summary.State);
    }

    // ── images.json → ImageSummary ─────────────────────────────────────────────────────────────

    [Fact]
    public void Size_is_read_from_the_human_string_not_blob_size()
    {
        var image = Assert.Single(Images(), i => i.Tag == "1.27-alpine").ToImage();

        Assert.Equal(53_980_000L, image.SizeBytes);
    }

    [Fact]
    public void A_literal_none_tag_is_left_as_is()
    {
        var image = Assert.Single(Images(), i => i is { Repository: "nginx", Tag: "<none>" }).ToImage();

        Assert.Equal("<none>", image.Tag);
    }

    [Fact]
    public void A_real_tag_survives_mapping()
    {
        // images.json's nginx row carries a concrete tag, "1.27-alpine" — unlike the "<none>" case
        // above, this differs from ImageSummary.Tag's own default, so it actually pins that ToImage
        // reads Tag from the DTO rather than only ever hitting the SDK's default.
        var image = Assert.Single(Images(), i => i is { Repository: "nginx", Tag: "1.27-alpine" }).ToImage();

        Assert.Equal("1.27-alpine", image.Tag);
    }

    // ── network-ls.json → NetworkSummary ───────────────────────────────────────────────────────

    [Fact]
    public void Kindnet_host_and_none_all_share_an_empty_id_so_lookup_must_use_name()
    {
        var networks = Networks().Select(n => n.ToNetwork()).ToList();

        Assert.All(networks, n => Assert.Equal(string.Empty, n.Id));
        Assert.Equal(ExpectedNetworkNames, networks.Select(n => n.Name).ToArray());
        // Indexing by Id would collapse all three into one entry; Name is the only usable key.
        Assert.Single(networks.Select(n => n.Id).Distinct());
        Assert.Equal(3, networks.Select(n => n.Name).Distinct().Count());
    }

    [Fact]
    public void Host_and_none_are_recognized_as_built_in()
    {
        var networks = Networks().Select(n => n.ToNetwork()).ToDictionary(n => n.Name);

        Assert.True(networks["host"].IsBuiltIn);
        Assert.True(networks["none"].IsBuiltIn);
        Assert.False(networks["kindnet"].IsBuiltIn);
    }

    [Fact]
    public void Driver_is_never_guessed_as_bridge_for_an_unknown_network()
    {
        // nerdctl's `network ls` reports no driver at all. "host" and "none" are self-evident (the
        // network's own name is its driver); "kindnet" is not — claiming "bridge" for it (the SDK's own
        // NetworkSummary.Driver default) would be a wrong answer stated as fact, not a missing one.
        var networks = Networks().Select(n => n.ToNetwork()).ToDictionary(n => n.Name);

        Assert.Equal("host", networks["host"].Driver);
        Assert.Equal("none", networks["none"].Driver);
        Assert.Equal(string.Empty, networks["kindnet"].Driver);
    }

    // ── inspect.json → ContainerInspect ────────────────────────────────────────────────────────

    [Fact]
    public void Inspect_state_status_maps_the_same_way_dockers_does()
    {
        var inspect = Inspect().ToInspect();

        Assert.Equal(ContainerState.Running, inspect.State);
        Assert.Equal("running", inspect.Status);
    }

    [Fact]
    public void Empty_top_level_name_falls_back_to_the_cri_container_name_label()
    {
        // The captured container's top-level "Name" is "" — nerdctl's CRI plugin never sets it.
        var inspect = Inspect().ToInspect();

        Assert.Equal("local-path-provisioner", inspect.Name);
    }

    /// <summary>
    /// The captured container publishes nothing, and its <c>HostConfig.PortBindings</c> is <c>{}</c> —
    /// so the key really is in this payload, and an empty one means no ports rather than a mapping the
    /// adapter failed to read (KON-369).
    /// </summary>
    [Fact]
    public void An_empty_port_bindings_map_means_no_published_ports()
    {
        Assert.Empty(Inspect().ToInspect().Ports);
    }

    /// <summary>
    /// Populated bindings, constructed the way the other HostConfig edge cases in this file are: the
    /// capture has none, and this payload is Docker-shaped, so the pairing follows Docker's own.
    /// </summary>
    [Fact]
    public void Published_ports_are_read_from_host_config()
    {
        var inspect = new NerdctlInspectContainer
        {
            Id = "abc",
            Config = new NerdctlInspectConfig { Image = "nginx:alpine" },
            State = new NerdctlInspectState { Status = "exited" },
            HostConfig = new NerdctlInspectHostConfig
            {
                PortBindings = new Dictionary<string, List<NerdctlInspectPortBinding>?>
                {
                    ["80/tcp"] =
                    [
                        new NerdctlInspectPortBinding { HostIp = "0.0.0.0", HostPort = "8080" },
                        new NerdctlInspectPortBinding { HostIp = "::", HostPort = "8080" },
                    ],
                    // Asked for a random host port: nothing to carry over.
                    ["443/tcp"] = [new NerdctlInspectPortBinding { HostPort = string.Empty }],
                },
            },
        }.ToInspect();

        var port = Assert.Single(inspect.Ports);
        Assert.Equal((8080, 80, "tcp"), (port.HostPort, port.ContainerPort, port.Protocol));
    }

    [Fact]
    public void Zero_memory_means_no_limit()
    {
        var inspect = Inspect().ToInspect();

        Assert.Null(inspect.MemoryLimitBytes);
    }

    [Fact]
    public void A_positive_memory_is_read_from_host_config()
    {
        // inspect.json's captured container has HostConfig.Memory == 0 (no limit), so the fixture alone
        // cannot exercise the other branch — constructed directly, the same way the other HostConfig/
        // Config edge cases in this file are, to pin that a real limit survives mapping rather than the
        // line only ever being seen returning null.
        var inspect = new NerdctlInspectContainer
        {
            HostConfig = new NerdctlInspectHostConfig { Memory = 536_870_912 },
        }.ToInspect();

        Assert.Equal(536_870_912, inspect.MemoryLimitBytes);
    }

    [Fact]
    public void Mounts_and_networks_carry_over()
    {
        var inspect = Inspect().ToInspect();

        Assert.Contains(inspect.Mounts, m => m is { Destination: "/etc/hosts", ReadWrite: true });
        var network = Assert.Single(inspect.Networks);
        Assert.Equal("10.244.0.3", network.IpAddress);
        // No Gateway key was present on this container's network endpoint at all.
        Assert.Equal(string.Empty, network.Gateway);
    }

    [Fact]
    public void Oom_killed_is_always_false_since_nerdctl_does_not_report_it()
    {
        // This pins nothing: false is both the absent JSON key's and ContainerInspect.OomKilled's own
        // default, so ToInspect could drop the assignment entirely and this would still pass. Kept as
        // documentation of the gap, not as a test that would catch a regression here.
        Assert.False(Inspect().ToInspect().OomKilled);
    }

    [Fact]
    public void Environment_variables_are_split_on_the_first_equals_sign()
    {
        // inspect.json's Config.Env carries 13 real entries; PATH's own value has colons but no '=',
        // so this pins the ordinary case against real captured data.
        var inspect = Inspect().ToInspect();

        Assert.Equal(13, inspect.EnvironmentVariables.Count);
        Assert.Equal("local-path-provisioner-855c7b7774-vw7t9", inspect.EnvironmentVariables["HOSTNAME"]);
    }

    [Fact]
    public void An_env_value_containing_an_equals_sign_keeps_the_rest_intact()
    {
        // None of inspect.json's 13 real Env entries happens to contain a second '=' in its value, so
        // the fixture alone cannot exercise this edge — constructed directly to pin the same
        // split-on-first-'=' behavior NerdctlJson.Labels already handles for comma-joined labels.
        var inspect = new NerdctlInspectContainer
        {
            Config = new NerdctlInspectConfig { Env = ["CONNECTION_STRING=Server=localhost;User=admin"] },
        }.ToInspect();

        Assert.Equal("Server=localhost;User=admin", inspect.EnvironmentVariables["CONNECTION_STRING"]);
    }

    [Fact]
    public void Empty_started_at_and_finished_at_map_to_null()
    {
        // The captured container's State.StartedAt/FinishedAt are both "" — nerdctl's own convention
        // for "unset", unlike Docker's zero-date. Confirmed against real fixture data.
        var inspect = Inspect().ToInspect();

        Assert.Null(inspect.StartedAt);
        Assert.Null(inspect.FinishedAt);
    }

    [Fact]
    public void A_real_started_at_is_not_also_treated_as_unset()
    {
        // inspect.json's StartedAt is always "" on the captured container, so the fixture alone cannot
        // exercise ParseOptionalTime's other branch — constructed directly to pin that a real timestamp
        // survives rather than being swallowed by the same empty-string check.
        var inspect = new NerdctlInspectContainer
        {
            State = new NerdctlInspectState { StartedAt = "2026-08-02T08:42:05.000000000Z" },
        }.ToInspect();

        Assert.Equal(new DateTimeOffset(2026, 8, 2, 8, 42, 5, TimeSpan.Zero), inspect.StartedAt);
    }

    // ── stats ───────────────────────────────────────────────────────────────────────────────────

    private static NerdctlStats StatsRow() =>
        JsonSerializer.Deserialize<NerdctlStats>(Fixture("stats.json"), Options)!;

    [Fact]
    public void ToStats_reads_every_paired_field_of_a_real_stats_row()
    {
        var stats = StatsRow().ToStats("statsprobe");

        Assert.Equal(0, stats.CpuPercent);
        // 13.11MiB used of 62.7GiB — binary units, so the used figure is above 13 million bytes and the
        // limit above 60 billion. Reading these with the decimal parser would land ~5% low on both.
        Assert.InRange(stats.MemoryUsedBytes, 13_000_000, 14_000_000);
        Assert.InRange(stats.MemoryLimitBytes, 60_000_000_000, 70_000_000_000);
        Assert.Equal(0, stats.NetRxBytes);
        Assert.Equal(0, stats.NetTxBytes);
        Assert.Equal(0, stats.BlockReadBytes);
        Assert.Equal(0, stats.BlockWriteBytes);
    }

    [Fact]
    public void ToStats_carries_the_id_the_caller_asked_about_not_nerdctls_short_one()
    {
        // The fixture's own ID is the 12-character "2b31b8a09e84"; a caller streaming by full id would
        // never match its samples back up if that were used instead.
        var stats = StatsRow().ToStats("2b31b8a09e8478f1b1e0e1f0f9b0c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f70819");

        Assert.Equal("2b31b8a09e8478f1b1e0e1f0f9b0c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f70819", stats.ContainerId);
    }

    [Fact]
    public void ToStats_leaves_a_field_nerdctl_did_not_print_at_zero_rather_than_inventing_one()
    {
        var stats = new NerdctlStats().ToStats("empty");

        Assert.Equal(0, stats.CpuPercent);
        Assert.Equal(0, stats.MemoryUsedBytes);
        Assert.Equal(0, stats.MemoryLimitBytes);
    }

    // ── events ──────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<NerdctlEvent> EventRows() =>
        NerdctlJson.Parse<NerdctlEvent>(Fixture("events.ndjson"));

    [Fact]
    public void ToEvent_takes_the_id_from_the_nested_payload_because_the_records_own_ID_is_empty()
    {
        var created = EventRows().Single(e => e.Topic == "/containers/create");

        Assert.Equal(string.Empty, created.Id);

        var mapped = created.ToEvent();

        Assert.Equal("62091b25…", mapped.ResourceId);
        Assert.Equal(EngineEventType.Created, mapped.Type);
        Assert.Equal(ResourceKind.Container, mapped.ResourceKind);
        // The record's own Timestamp is ISO8601 with nanoseconds; pinned to the second so the assertion
        // does not depend on how many fractional digits survive parsing.
        Assert.Equal(
            new DateTime(2026, 8, 3, 12, 28, 22, DateTimeKind.Utc),
            new DateTime(mapped.Timestamp.UtcDateTime.Ticks - (mapped.Timestamp.UtcDateTime.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc));
    }

    [Fact]
    public void ToEvent_maps_containerds_storage_topics_to_Unknown_rather_than_inventing_an_action()
    {
        var snapshot = EventRows().Single(e => e.Topic == "/snapshot/prepare").ToEvent();

        Assert.Equal(EngineEventType.Unknown, snapshot.Type);
        // The payload names its subject "key", not "id" — still mapped, so a snapshot event is not
        // silently id-less.
        Assert.Equal("62091b25…", snapshot.ResourceId);
    }

    [Theory]
    [InlineData("/tasks/start", EngineEventType.Started, ResourceKind.Container)]
    [InlineData("/tasks/exit", EngineEventType.Died, ResourceKind.Container)]
    [InlineData("/tasks/delete", EngineEventType.Stopped, ResourceKind.Container)]
    [InlineData("/tasks/paused", EngineEventType.Paused, ResourceKind.Container)]
    [InlineData("/tasks/resumed", EngineEventType.Unpaused, ResourceKind.Container)]
    [InlineData("/containers/delete", EngineEventType.Removed, ResourceKind.Container)]
    [InlineData("/images/create", EngineEventType.Pulled, ResourceKind.Image)]
    [InlineData("/images/delete", EngineEventType.Removed, ResourceKind.Image)]
    // Deliberately Unknown: containerd emits it alongside /containers/create for one user action, and
    // reporting both would show the same container being created twice.
    [InlineData("/tasks/create", EngineEventType.Unknown, ResourceKind.Container)]
    [InlineData("/leases/create", EngineEventType.Unknown, ResourceKind.Container)]
    public void ToEvent_maps_containerd_topics_not_docker_actions(
        string topic, EngineEventType expectedType, ResourceKind expectedKind)
    {
        var mapped = new NerdctlEvent { Topic = topic }.ToEvent();

        Assert.Equal(expectedType, mapped.Type);
        Assert.Equal(expectedKind, mapped.ResourceKind);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("die")]
    [InlineData("stop")]
    public void Docker_action_names_are_not_topics_and_map_to_nothing(string dockerAction)
    {
        // Pins the trap: matching on Docker's vocabulary finds nothing here, without any error to say so.
        Assert.Equal(EngineEventType.Unknown, new NerdctlEvent { Topic = dockerAction }.ToEvent().Type);
    }
}
