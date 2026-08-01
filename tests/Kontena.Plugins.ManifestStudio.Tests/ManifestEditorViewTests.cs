using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Kontena.Plugins.ManifestStudio.Views;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// The runtime spike Plans/manifest-studio.md §11 asks for before ticket 1 stacks features on top:
/// AvaloniaEdit compiling against the app's pinned Avalonia 12.0.3 says nothing about whether it
/// resolves and paints at runtime (DataTray hit exactly this gap once already).
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ManifestEditorViewTests(HeadlessSessionFixture headless)
{
    private const string SampleYaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: sample\n";

    [Fact]
    public Task Loads_yaml_and_renders_a_text_run() =>
        headless.Session.Dispatch(
            () =>
            {
                var view = new ManifestEditorView { Text = SampleYaml };
                var window = new Window { Width = 400, Height = 300, Content = view };
                window.Show();

                for (var i = 0; i < 3; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Assert.Equal(SampleYaml, view.Text);
                Assert.NotEmpty(view.GetVisualDescendants().OfType<TextView>());
            },
            CancellationToken.None);
}
