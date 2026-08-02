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

    [Fact]
    public void Zero_memory_means_no_limit()
    {
        var inspect = Inspect().ToInspect();

        Assert.Null(inspect.MemoryLimitBytes);
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
        Assert.False(Inspect().ToInspect().OomKilled);
    }
}
