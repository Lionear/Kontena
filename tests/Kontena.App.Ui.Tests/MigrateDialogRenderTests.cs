using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The migrate dialog draws what its view model decided (KON-350).
/// <para>
/// Against the built control rather than the view model, because the view model half is covered by
/// <c>MigrateContainerViewModelTests</c> and was never the risk. The risk here is a dialog that
/// renders a plan nobody can act on: a Migrate button still enabled on a blocked plan, or a preview
/// bound to nothing. Neither shows up in a build.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class MigrateDialogRenderTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Fact]
    public Task A_blocked_plan_renders_its_reason_and_leaves_the_button_disabled() =>
        Session.Dispatch(async () =>
        {
            // "api-gateway" is a name the fake engine seeds, so it is taken on the target as well.
            var model = await ModelAsync("api-gateway");
            var window = Show(model);

            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            var migrate = buttons.Single(b => b.Content is "Migrate");

            Assert.False(migrate.IsEnabled);
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
                text => text is not null && text.Contains("already has a container", StringComparison.Ordinal));
        }, CancellationToken.None);

    [Fact]
    public Task The_preview_and_the_dropped_lines_are_on_screen() =>
        Session.Dispatch(async () =>
        {
            var model = await ModelAsync("web");
            var window = Show(model);

            var preview = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            Assert.Equal(model.CommandPreview, preview.Text);

            // The "what does not come along" column is the reason this dialog is not just a button.
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
                text => text is not null && text.Contains("Not inspected", StringComparison.Ordinal));
        }, CancellationToken.None);

    private static Window Show(MigrateContainerViewModel model)
    {
        var window = new Window
        {
            Width = 760,
            Height = 800,
            Content = new MigrateContainerView { DataContext = model },
        };

        window.Show();

        for (var i = 0; i < 3; i++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return window;
    }

    private static async Task<MigrateContainerViewModel> ModelAsync(string name)
    {
        var source = new FakeEngine(seed: false, backend: "docker", displayName: "Docker");

        var id = await source.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "nginx:1.27-alpine",
            Name = name,
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
