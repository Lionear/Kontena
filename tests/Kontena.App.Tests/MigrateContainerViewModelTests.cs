using Kontena.App.ViewModels;
using Kontena.Core.Migration;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The migrate dialog (KON-350). What is pinned here is which engines it offers, that a blocked plan
/// has no run button, and that the preview describes what will actually be created.
/// </summary>
public sealed class MigrateContainerViewModelTests
{
    /// <summary>
    /// The dialog only appears where it can do something: a second engine backend that is connected
    /// and can create containers. On a machine with one engine there is nothing to migrate to.
    /// </summary>
    [Fact]
    public async Task Targets_exclude_the_engine_the_container_lives_on()
    {
        var model = await ModelAsync();

        Assert.Equal(["apple"], model.Targets.Select(t => t.Backend));
    }

    /// <summary>A blocked plan has no run button — that is the whole point of blocking.</summary>
    [Fact]
    public async Task A_blocked_plan_disables_the_run_command()
    {
        // "api-gateway" is one of the names the fake engine seeds, so it is taken on the target too.
        var model = await ModelAsync(containerName: "api-gateway");

        Assert.False(model.MigrateCommand.CanExecute(null));
        Assert.Contains(model.Notes, n => n.Kind is MigrationNoteKind.Blocked);
    }

    /// <summary>
    /// Renaming is the way out of a taken name, so the plan is rebuilt when the name changes rather
    /// than leaving a blocker standing that no longer applies.
    /// </summary>
    [Fact]
    public async Task Renaming_rebuilds_the_plan()
    {
        var model = await ModelAsync(containerName: "api-gateway");

        model.ContainerName = "api-gateway-2";
        await model.RefreshPlanAsync();

        Assert.True(model.MigrateCommand.CanExecute(null));
        Assert.DoesNotContain(model.Notes, n => n.Kind is MigrationNoteKind.Blocked);
    }

    [Fact]
    public async Task The_command_preview_shows_what_will_be_created()
    {
        var model = await ModelAsync();

        Assert.Contains("--name", model.CommandPreview, StringComparison.Ordinal);
        Assert.Contains(model.Container.Image, model.CommandPreview, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whatever else the dialog shows, it says what will not come along. A screen that shows only
    /// ticks promises a completeness this cannot deliver.
    /// </summary>
    [Fact]
    public async Task The_plan_always_says_what_is_not_migrated()
    {
        var model = await ModelAsync();

        Assert.Contains(model.Dropped, n => n.Subject == "Not inspected");
    }

    /// <summary>
    /// A volume that exists on the target and holds data is left alone until someone says otherwise,
    /// and the row says which of the two it is.
    /// </summary>
    [Fact]
    public async Task A_volume_row_only_copies_once_overwrite_is_ticked()
    {
        var model = await ModelAsync(withVolume: "pgdata");

        var row = Assert.Single(model.Volumes);
        Assert.True(row.NeedsDecision);
        Assert.False(row.ToPlan().WillCopy);

        row.Overwrite = true;
        Assert.True(row.ToPlan().WillCopy);
    }

    /// <summary>
    /// The source engine seeds a container, the target is a second fake engine with its own seed.
    /// Both are real <see cref="FakeEngine"/>s: the dialog reads names, volumes and images off the
    /// target, and a stub that answers nothing would let every one of those reads rot.
    /// </summary>
    private static async Task<MigrateContainerViewModel> ModelAsync(
        string containerName = "web", string? withVolume = null)
    {
        var source = new FakeEngine(seed: false, backend: "docker", displayName: "Docker");

        var id = await source.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "nginx:1.27-alpine",
            Name = containerName,
            Mounts = withVolume is null
                ? []
                : [new MountSpec(MountSpec.Volume, withVolume, "/var/lib/postgresql/data")],
            Start = false,
        });

        var registry = new BackendRegistry(
        [
            new FakeEngineProvider(backend: "docker", displayName: "Docker"),
            new FakeEngineProvider(backend: "apple", displayName: "Apple container"),
        ]);

        var model = new MigrateContainerViewModel(
            source, registry, id, onClose: () => { }, onMigrated: () => Task.CompletedTask);

        await model.InitializeAsync();

        return model;
    }
}
