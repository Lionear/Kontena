using Kontena.App.ViewModels;
using Kontena.Engines.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Every destructive action asks first (KON-126).
/// <para>
/// The important assertion in each case is the one that runs <em>before</em> the confirm: nothing may
/// have happened yet. A dialog that appears after the volume is already gone is decoration. The second
/// half — that confirming still does the work — is there so a future "fix" cannot satisfy the first
/// half by breaking the button.
/// </para>
/// </summary>
public sealed class ConfirmDestructiveActionsTests
{
    /// <summary>Captures what a page asked, the way the shell would, without showing anything.</summary>
    private sealed class Asked
    {
        public ConfirmRequest? Request { get; private set; }

        public void Handle(ConfirmRequest request) => Request = request;

        public Task ConfirmAsync()
        {
            Assert.NotNull(Request);
            return Request.OnConfirm();
        }
    }

    [Fact]
    public async Task Deleting_a_volume_asks_before_anything_is_removed()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new VolumesViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        var row = page.Items[0];
        row.DeleteCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.True(asked.Request.Destructive);
        Assert.Contains(row.Name, asked.Request.Message, StringComparison.Ordinal);

        // The volume is still there — the click only asked.
        Assert.Contains(await engine.ListVolumesAsync(), v => v.Name == row.Name);

        await asked.ConfirmAsync();
        Assert.DoesNotContain(await engine.ListVolumesAsync(), v => v.Name == row.Name);
    }

    [Fact]
    public async Task A_volume_delete_with_nowhere_to_ask_does_nothing()
    {
        // The seam is optional in the type system, so this is the case that decides whether "not wired
        // up" degrades to a silent force-delete or to nothing at all. It has to be nothing.
        var engine = new FakeEngine();
        var page = new VolumesViewModel(engine);
        await page.LoadAsync();

        var name = page.Items[0].Name;
        page.Items[0].DeleteCommand.Execute(null);

        Assert.Contains(await engine.ListVolumesAsync(), v => v.Name == name);
    }

    [Fact]
    public async Task A_volume_confirm_names_what_would_lose_it()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new VolumesViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        var mounted = page.Items.First(v => v.MountedBy.Count > 0);
        mounted.DeleteCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains(mounted.MountedBy[0], asked.Request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_container_asks_first()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new ContainersViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        // The list holds Compose headings too now (KON-159); this test is about a container.
        var row = page.Items.OfType<ContainerRowViewModel>().First();
        row.RemoveCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains(row.Name, asked.Request.Message, StringComparison.Ordinal);
        Assert.Contains(await engine.ListContainersAsync(), c => c.Id == row.Id);

        await asked.ConfirmAsync();
        Assert.DoesNotContain(await engine.ListContainersAsync(), c => c.Id == row.Id);
    }

    [Fact]
    public async Task A_running_container_is_told_it_will_be_killed()
    {
        // The remove is forced, so "stop it first" is not advice the user gets to follow — the message
        // has to say what the button actually does.
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new ContainersViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        page.Items.OfType<ContainerRowViewModel>().First(c => c.IsRunning).RemoveCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains("killed", asked.Request.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_an_image_asks_first()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new ImagesViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        var row = page.Items[0];
        row.DeleteCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains(await engine.ListImagesAsync(), i => i.Id == row.Id);

        await asked.ConfirmAsync();
        Assert.DoesNotContain(await engine.ListImagesAsync(), i => i.Id == row.Id);
    }

    [Fact]
    public async Task Deleting_a_network_asks_first()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new NetworksViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        var row = page.Items.First(n => n.CanDelete);
        row.DeleteCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains(await engine.ListNetworksAsync(), n => n.Id == row.Id);

        await asked.ConfirmAsync();
        Assert.DoesNotContain(await engine.ListNetworksAsync(), n => n.Id == row.Id);
    }

    [Fact]
    public async Task Taking_a_compose_project_down_asks_first()
    {
        var engine = new FakeEngine();
        var asked = new Asked();
        var page = new ComposeProjectsViewModel(engine) { RequestConfirm = asked.Handle };
        await page.LoadAsync();

        Assert.NotEmpty(page.Items);
        var project = page.Items[0];
        project.DownCommand.Execute(null);

        Assert.NotNull(asked.Request);
        Assert.Contains(project.Name, asked.Request.Message, StringComparison.Ordinal);

        var before = await engine.ListContainersAsync();
        Assert.Contains(before, c => c.Id == project.ContainerIds[0]);

        await asked.ConfirmAsync();
        Assert.DoesNotContain(await engine.ListContainersAsync(), c => c.Id == project.ContainerIds[0]);
    }
}
