using Kontena.Core.Migration;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Core.Tests.Migration;

/// <summary>
/// The runner against two fake engines. What is pinned here is the order of the steps, that the
/// source survives, and that a failure halfway cleans up nothing it created.
/// </summary>
public sealed class ContainerMigrationRunnerTests : IDisposable
{
    private readonly string _staging =
        Path.Combine(Path.GetTempPath(), $"kon350-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_staging))
            Directory.Delete(_staging, recursive: true);
    }

    /// <summary>
    /// Copying a volume out of a running container gives a torn copy — a database halfway through a
    /// write. The stop is not politeness, it is what makes the copy worth having.
    /// </summary>
    [Fact]
    public async Task The_source_is_stopped_before_anything_is_copied()
    {
        var (source, target, plan, container) = await SetupAsync(running: true, withVolume: true);
        var runner = new ContainerMigrationRunner(source, target, _staging);

        var steps = await Collect(runner.RunAsync(plan, container));

        var stopped = steps.FindIndex(s => s.Step.Contains("Stopping", StringComparison.Ordinal));
        var copied = steps.FindIndex(s => s.Step.Contains("volume", StringComparison.OrdinalIgnoreCase));

        Assert.True(stopped >= 0 && copied > stopped);
    }

    /// <summary>Stopping is the only thing the migration does to the source. Ever.</summary>
    [Fact]
    public async Task The_source_container_is_never_removed()
    {
        var (source, target, plan, container) = await SetupAsync(running: true, withVolume: true);

        await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(plan, container));

        Assert.Contains(await source.ListContainersAsync(), c => c.Id == container.Id);
    }

    [Fact]
    public async Task Volume_contents_arrive_on_the_target()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: true);
        source.VolumeContents["data"] = [7, 7, 7];

        await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(plan, container));

        Assert.Equal<byte[]>([7, 7, 7], target.VolumeContents["data"]);
    }

    [Fact]
    public async Task A_volume_the_plan_skips_is_left_alone()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: true);
        source.VolumeContents["data"] = [7, 7, 7];
        target.VolumeContents["data"] = [1];

        var skipping = plan with
        {
            Volumes = [new VolumePlan("data", ExistsOnTarget: true, TargetHasData: true)],
        };

        await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(skipping, container));

        Assert.Equal<byte[]>([1], target.VolumeContents["data"]);
    }

    [Fact]
    public async Task The_container_is_created_stopped_and_its_id_comes_back()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: false);

        var steps = await Collect(
            new ContainerMigrationRunner(source, target, _staging).RunAsync(plan, container));

        Assert.False(Assert.Single(target.CreatedRequests).Start);
        Assert.NotNull(steps[^1].ContainerId);
    }

    /// <summary>The staging is ours and it is a copy, so it is the one thing that is cleaned up.</summary>
    [Fact]
    public async Task The_staging_directory_is_gone_afterwards()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: true);

        await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(plan, container));

        Assert.False(Directory.Exists(_staging));
    }

    /// <summary>
    /// Cleaning up is removing, and removing asks first. What a failed run left behind is named in
    /// the error instead, so the second attempt asks about it as an ordinary overwrite question.
    /// </summary>
    [Fact]
    public async Task A_failure_halfway_removes_nothing_it_created()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: true);
        target.FailOn = nameof(IContainerEngine.CreateContainerAsync);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(plan, container)));

        Assert.Contains("data", target.VolumeContents.Keys);
        Assert.Contains(await target.ListVolumesAsync(), v => v.Name == "data");
        Assert.False(Directory.Exists(_staging));
    }

    /// <summary>
    /// A plan with a blocker never had a run button. Reaching the runner with one is a programming
    /// error, and it says so instead of doing half a migration.
    /// </summary>
    [Fact]
    public async Task A_blocked_plan_is_refused()
    {
        var (source, target, plan, container) = await SetupAsync(running: false, withVolume: false);

        var blocked = plan with
        {
            Notes = [new MigrationNote(MigrationNoteKind.Blocked, "Name", "taken")],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Collect(new ContainerMigrationRunner(source, target, _staging).RunAsync(blocked, container)));
    }

    private static async Task<List<MigrationProgress>> Collect(IAsyncEnumerable<MigrationProgress> steps)
    {
        var collected = new List<MigrationProgress>();
        await foreach (var step in steps)
            collected.Add(step);
        return collected;
    }

    /// <summary>
    /// Two fakes, a container that really exists on the source one, and a plan that recreates it. The
    /// container is created rather than invented so that "the source is never removed" has something
    /// real to look for afterwards.
    /// </summary>
    private static async Task<(FakeEngine Source, FakeEngine Target, MigrationPlan Plan, ContainerInspect Container)>
        SetupAsync(bool running, bool withVolume)
    {
        var source = new FakeEngine(seed: false, backend: "source", displayName: "Source");
        var target = new FakeEngine(seed: false, backend: "target", displayName: "Target");

        var id = await source.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Name = "app",
            Start = running,
        });

        var mounts = withVolume
            ? new List<MountSpec> { new(MountSpec.Volume, "data", "/data") }
            : [];

        var container = new ContainerInspect
        {
            Id = id,
            Name = "app",
            Image = "alpine:3.20",
            ImageId = "sha256:abc",
            State = running ? ContainerState.Running : ContainerState.Exited,
        };

        var plan = new MigrationPlan
        {
            Request = new CreateContainerRequest
            {
                Image = "alpine:3.20",
                Name = "app",
                Mounts = mounts,
                Start = false,
            },
            Notes = [],
            Volumes = withVolume
                ? [new VolumePlan("data", ExistsOnTarget: false, TargetHasData: false)]
                : [],
        };

        return (source, target, plan, container);
    }
}
