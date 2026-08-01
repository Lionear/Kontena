using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// The tree, tabs and editor actually wire together (KON-287) — the folder picker is the one thing
/// left untested here, since it needs a real storage provider (Notes.md "Handmatig testen vóór de
/// tag"); everything downstream of an already-opened workspace is exercised through the real view.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class WorkspaceViewTests(HeadlessSessionFixture headless) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-view-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private (Window Window, WorkspaceView View, WorkspaceViewModel ViewModel) Show()
    {
        var vm = new WorkspaceViewModel(ManifestWorkspace.Open(_root));
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Settle();
        return (window, view, vm);
    }

    [Fact]
    public Task Double_tapping_a_file_node_opens_it_as_a_tab() =>
        headless.Session.Dispatch(
            () =>
            {
                File.WriteAllText(Path.Combine(_root, "deployment.yaml"), "kind: Deployment\n");
                var (_, view, vm) = Show();

                var node = view.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == "deployment.yaml");
                node.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
                Settle();

                var document = Assert.Single(vm.OpenTabs);
                Assert.Equal("deployment.yaml", document.Name);
                Assert.Same(document, vm.ActiveDocument);
                Assert.Equal("kind: Deployment\n", document.Text);
            },
            CancellationToken.None);

    [Fact]
    public Task Editing_in_the_open_tab_marks_it_dirty_and_saving_writes_the_file() =>
        headless.Session.Dispatch(
            () =>
            {
                var path = Path.Combine(_root, "deployment.yaml");
                File.WriteAllText(path, "kind: Deployment\n");
                var (_, view, vm) = Show();
                var document = vm.Open(path);
                Settle();

                var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
                editor.Text = "kind: StatefulSet\n";
                Settle();

                Assert.True(document.IsDirty);
                Assert.Equal("kind: StatefulSet\n", document.Text);

                var save = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Save"));
                save.Command!.Execute(null);

                Assert.False(document.IsDirty);
                Assert.Equal("kind: StatefulSet\n", File.ReadAllText(path));
            },
            CancellationToken.None);

    [Fact]
    public Task Closing_a_tab_removes_it_from_the_strip() =>
        headless.Session.Dispatch(
            () =>
            {
                var path = Path.Combine(_root, "deployment.yaml");
                File.WriteAllText(path, "kind: Deployment\n");
                var (_, view, vm) = Show();
                vm.Open(path);
                Settle();

                var close = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "x"));
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Empty(vm.OpenTabs);
            },
            CancellationToken.None);
}
