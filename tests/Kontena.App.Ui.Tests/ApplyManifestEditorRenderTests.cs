using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the editor's ceiling draws (KON-380). The truncation itself is covered against the raw
/// view-model in <c>Kontena.App.Tests</c>; what only a render catches is the banner stealing the
/// editor's height, or a binding to a property the view cannot see.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ApplyManifestEditorRenderTests(HeadlessSessionFixture headless)
{
    private static (ApplyManifestViewModel Vm, ApplyManifestView View) Render(string yaml)
    {
        var vm = new ApplyManifestViewModel(new FakeClusterEngine(), "kind-test") { YamlText = yaml };
        var view = new ApplyManifestView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (vm, view);
    }

    private static TextBox Editor(ApplyManifestView view) =>
        view.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("editor"));

    [Fact]
    public Task An_ordinary_bundle_gets_an_editable_box_and_no_banner() =>
        headless.Session.Dispatch(() =>
        {
            var (_, view) = Render("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: a\n");
            var editor = Editor(view);

            Assert.False(editor.IsReadOnly);
            Assert.DoesNotContain(
                view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                t => t.Text?.Contains("Too large to edit", StringComparison.Ordinal) == true);
        }, CancellationToken.None);

    [Fact]
    public Task A_render_too_big_to_lay_out_says_so_and_still_leaves_the_editor_room() =>
        headless.Session.Dispatch(() =>
        {
            var (_, view) = Render(new string('y', 3 * 1024 * 1024));
            var editor = Editor(view);

            Assert.True(editor.IsReadOnly);
            Assert.Contains(
                view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                t => t.Text?.Contains("Too large to edit", StringComparison.Ordinal) == true);

            // The banner is one line; it must not swallow the pane it sits above.
            Assert.True(editor.Bounds.Height > 400, $"editor collapsed to {editor.Bounds.Height}px");
        }, CancellationToken.None);
}
