using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// <see cref="AppleEngine"/>'s read side, against fixtures captured from a real <c>container</c> 1.2.2
/// on macOS 26.6 (Depot kontena/Notes/apple-container-cli-formats.md) — never hand-written JSON, which
/// would only prove the mapper agrees with whatever this file assumed.
/// <para>
/// The rig behind the fixtures: a running container <c>web</c> with two published ports (one udp), two
/// labels and a named volume; a stopped container <c>batch</c> on a user-created network.
/// </para>
/// </summary>
public sealed class AppleEngineReadTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    /// <summary>Answers the container list for anything that asks for it, and the given output for the
    /// command under test — the read methods each need both, because "used by" and "in use" are
    /// answered from the containers.</summary>
    private static FakeToolRunner Runner(string firstArgument, string output) =>
        Installed()
            .When(i => i.Arguments.Count > 0 && i.Arguments[0] == firstArgument, output: [output])
            .When(i => i.Arguments.Count > 0 && i.Arguments[0] == "list", output: [Fixture("list.json")]);

    // ── Containers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListContainersAsync_maps_both_rows_from_the_real_capture()
    {
        var runner = Runner("list", Fixture("list.json"));

        var containers = await Engine(runner).ListContainersAsync();

        Assert.Equal(2, containers.Count);
        var web = Assert.Single(containers, c => c.Id == "web");
        Assert.Equal(ContainerState.Running, web.State);
        Assert.Equal("docker.io/library/alpine:3.20", web.Image);
        Assert.Equal("apple", web.Backend);
        Assert.Equal(ContainerState.Exited, Assert.Single(containers, c => c.Id == "batch").State);
    }

    /// <summary>
    /// The CLI has no separate name field: what it calls the id is the name the user gave. A mapper that
    /// left <c>Name</c> empty would still list the right number of rows, so this is asserted rather than
    /// assumed.
    /// </summary>
    [Fact]
    public async Task ListContainersAsync_carries_the_id_as_the_name()
    {
        var runner = Runner("list", Fixture("list.json"));

        var containers = await Engine(runner).ListContainersAsync();

        Assert.All(containers, c => Assert.Equal(c.Id, c.Name));
    }

    /// <summary>The protocol field is <c>proto</c>, not <c>protocol</c>. Reading the wrong one yields a
    /// silently empty string rather than an error, so the udp mapping is what proves it.</summary>
    [Fact]
    public async Task ListContainersAsync_reads_published_ports_including_the_protocol()
    {
        var runner = Runner("list", Fixture("list.json"));

        var web = Assert.Single(await Engine(runner).ListContainersAsync(), c => c.Id == "web");

        Assert.Equal(2, web.Ports.Count);
        Assert.Contains(web.Ports, p => p is { HostPort: 8080, ContainerPort: 80, Protocol: "tcp" });
        Assert.Contains(web.Ports, p => p is { HostPort: 9090, ContainerPort: 90, Protocol: "udp" });
    }

    [Fact]
    public async Task ListContainersAsync_reads_labels_as_a_map()
    {
        var runner = Runner("list", Fixture("list.json"));

        var web = Assert.Single(await Engine(runner).ListContainersAsync(), c => c.Id == "web");

        Assert.Equal("demo", web.Labels["app"]);
        Assert.Equal("front", web.Labels["tier"]);
    }

    /// <summary><c>--all</c> is what makes a stopped container appear; asking without it and still
    /// showing "no containers" next to one that exists is the failure this guards.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ListContainersAsync_asks_for_stopped_containers_only_when_told_to(
        bool all, bool expectsAllFlag)
    {
        var runner = Runner("list", Fixture("list.json"));

        await Engine(runner).ListContainersAsync(all);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(expectsAllFlag, invocation.Arguments.Contains("--all"));
    }

    // ── Inspect ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InspectContainerAsync_reads_the_detail_from_the_same_shape_as_the_list()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        Assert.Equal("web", detail.Id);
        Assert.Equal("docker.io/library/alpine:3.20", detail.Image);
        Assert.StartsWith("sha256:", detail.ImageId);
        Assert.Equal(ContainerState.Running, detail.State);
        Assert.NotNull(detail.StartedAt);
        Assert.True(detail.MemoryLimitBytes > 0);
    }

    /// <summary>
    /// The inspect carries the published ports as well as the list does. Anything that re-creates this
    /// container reads them there, because on other engines the list only reports them while the
    /// container runs (KON-369).
    /// </summary>
    [Fact]
    public async Task InspectContainerAsync_carries_the_published_ports()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        Assert.Equal(
            [(8080, 80, "tcp"), (9090, 90, "udp")],
            detail.Ports.Select(p => (p.HostPort, p.ContainerPort, p.Protocol)));
    }

    [Fact]
    public async Task InspectContainerAsync_joins_the_init_process_into_a_command()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        Assert.StartsWith("sh -c", detail.Command);
        Assert.Equal("/", detail.WorkingDirectory);
        Assert.Equal("0", detail.User);
    }

    /// <summary>Environment arrives as OCI <c>KEY=value</c> strings, not a map.</summary>
    [Fact]
    public async Task InspectContainerAsync_splits_the_environment_into_a_map()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        Assert.Contains("PATH", detail.EnvironmentVariables.Keys);
        Assert.DoesNotContain("=", detail.EnvironmentVariables.Keys.First());
    }

    /// <summary>
    /// A named volume must report the volume's name, not the path of the disk image behind it: the path
    /// is where Apple keeps volumes, and the Volumes page lists names.
    /// </summary>
    [Fact]
    public async Task InspectContainerAsync_names_the_volume_a_mount_uses()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        var mount = Assert.Single(detail.Mounts);
        Assert.Equal("volume", mount.Type);
        Assert.Equal("kon31-vol", mount.Source);
        Assert.Equal("/data", mount.Destination);
    }

    /// <summary>The address is printed in CIDR form; a "/24" in an IP column reads as a subnet.</summary>
    [Fact]
    public async Task InspectContainerAsync_strips_the_prefix_length_from_the_address()
    {
        var runner = Runner("inspect", Fixture("inspect-web.json"));

        var detail = await Engine(runner).InspectContainerAsync("web");

        var network = Assert.Single(detail.Networks);
        Assert.DoesNotContain("/", network.IpAddress);
        Assert.NotEmpty(network.Gateway);
    }

    /// <summary>An empty array means the CLI answered about nothing; a blank detail page would be the
    /// wrong way to say so.</summary>
    [Fact]
    public async Task InspectContainerAsync_reports_an_empty_answer_as_missing()
    {
        var runner = Runner("inspect", "[]");

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            async () => await Engine(runner).InspectContainerAsync("gone"));
    }

    // ── Images ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListImagesAsync_splits_the_reference_into_repository_and_tag()
    {
        var runner = Runner("image", Fixture("images.json"));

        var image = Assert.Single(await Engine(runner).ListImagesAsync());

        Assert.Equal("docker.io/library/alpine", image.Repository);
        Assert.Equal("3.20", image.Tag);
    }

    /// <summary>
    /// The size lives per platform variant and a multi-arch index also carries ~79 KB attestation
    /// entries whose platform is <c>unknown</c>. Counting those would roughly double every multi-arch
    /// image; picking the wrong variant would report another architecture's size.
    /// </summary>
    [Fact]
    public async Task ListImagesAsync_reports_a_real_platform_variant_as_the_size()
    {
        var runner = Runner("image", Fixture("images.json"));

        var image = Assert.Single(await Engine(runner).ListImagesAsync());

        // Every attestation entry in the capture is under 100 KB and every real variant is over 3 MB.
        Assert.True(image.SizeBytes > 1_000_000, $"size was {image.SizeBytes}");
    }

    /// <summary>"In use" is not a field this CLI prints — it is whether a container was created from
    /// that reference, which only the container list can answer.</summary>
    [Fact]
    public async Task ListImagesAsync_marks_an_image_a_container_runs_as_in_use()
    {
        var runner = Runner("image", Fixture("images.json"));

        var image = Assert.Single(await Engine(runner).ListImagesAsync());

        Assert.True(image.InUse);
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListVolumesAsync_answers_used_by_from_the_containers_that_mount_it()
    {
        var runner = Runner("volume", Fixture("volumes.json"));

        var volume = Assert.Single(await Engine(runner).ListVolumesAsync());

        Assert.Equal("kon31-vol", volume.Name);
        Assert.Equal(["web"], volume.UsedBy);
        Assert.False(volume.IsDangling);
    }

    /// <summary>
    /// <c>sizeInBytes</c> is the size the sparse disk image may grow to — 512 GiB on a volume with
    /// nothing in it. Carrying it over would put that number in a "size" column.
    /// </summary>
    [Fact]
    public async Task ListVolumesAsync_does_not_report_the_allocated_size_as_a_size()
    {
        var runner = Runner("volume", Fixture("volumes.json"));

        var volume = Assert.Single(await Engine(runner).ListVolumesAsync());

        Assert.Null(volume.SizeBytes);
        Assert.NotEmpty(volume.Mountpoint);
    }

    // ── Networks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNetworksAsync_reads_the_subnet_from_the_status()
    {
        var runner = Runner("network", Fixture("networks.json"));

        var networks = await Engine(runner).ListNetworksAsync();

        var builtIn = Assert.Single(networks, n => n.Id == "default");
        Assert.NotNull(builtIn.Subnet);
        Assert.Contains("/", builtIn.Subnet);
    }

    /// <summary>The built-in network is recognised by Apple's own label, not by being called
    /// "default" — a user-created network could carry that name and this label cannot.</summary>
    [Fact]
    public async Task ListNetworksAsync_recognises_the_built_in_network_by_its_label()
    {
        var runner = Runner("network", Fixture("networks.json"));

        var networks = await Engine(runner).ListNetworksAsync();

        Assert.True(Assert.Single(networks, n => n.Id == "default").IsBuiltIn);
        Assert.False(Assert.Single(networks, n => n.Id == "kon31-net").IsBuiltIn);
    }

    /// <summary>
    /// Attachment is read from the configuration, not the status: a stopped container still belongs to
    /// its network, and <c>batch</c> — stopped, on the user-created network — is exactly that case.
    /// </summary>
    [Fact]
    public async Task ListNetworksAsync_counts_a_stopped_container_as_attached()
    {
        var runner = Runner("network", Fixture("networks.json"));

        var networks = await Engine(runner).ListNetworksAsync();

        Assert.Equal(["batch"], Assert.Single(networks, n => n.Id == "kon31-net").AttachedContainers);
        Assert.Equal(["web"], Assert.Single(networks, n => n.Id == "default").AttachedContainers);
    }

    // ── Info ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>system version</c> lists the apiserver alongside the CLI, and the apiserver's version field is
    /// a whole sentence. Taking the first entry would put that sentence in the title bar.
    /// </summary>
    [Fact]
    public async Task GetInfoAsync_reports_the_cli_version_not_the_apiserver_sentence()
    {
        var runner = Runner("system", Fixture("version.json"));

        var info = await Engine(runner).GetInfoAsync();

        Assert.Equal("1.2.2", info.Version);
        Assert.Equal("Apple container", info.DisplayName);
        Assert.Equal(EngineConnectionState.Connected, info.ConnectionState);
    }

    /// <summary>A missing binary is not a failed command: it is the runtime not being here at all, and
    /// the switcher has a row for that.</summary>
    [Fact]
    public async Task PingAsync_reports_a_missing_binary_as_unreachable()
    {
        var runner = new FakeToolRunner();

        await Assert.ThrowsAsync<EngineUnreachableException>(
            async () => await Engine(runner).PingAsync());
    }
}
