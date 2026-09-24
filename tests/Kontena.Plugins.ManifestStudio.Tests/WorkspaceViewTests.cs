using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

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

    /// <summary>
    /// The schema the editor validates against comes from the index the workspace was handed
    /// (KON-296): the document says what it is, the index answers, and neither KON-290's completion nor
    /// KON-291's diagnostics can light up before that hand-off works.
    /// </summary>
    [Fact]
    public Task The_editor_is_given_the_schema_for_the_kind_the_document_declares() =>
        headless.Session.Dispatch(
            () =>
            {
                var source = new Schemas.FakeClusterSchemaSource("v1.31.0");
                source.Documents[("apps", "v1")] = """
                {
                  "components": {
                    "schemas": {
                      "io.k8s.api.apps.v1.Deployment": {
                        "type": "object",
                        "x-kubernetes-group-version-kind":
                          [{ "group": "apps", "version": "v1", "kind": "Deployment" }]
                      }
                    }
                  }
                }
                """;

                var (_, view, _) = Show();
                view.Schemas = new SchemaIndex(source);

                view.Editor.Text = "apiVersion: apps/v1\nkind: Deployment\n";
                Settle();

                Assert.NotNull(view.Editor.Schema);

                // A keystroke that leaves apiVersion/kind alone must not re-ask the cluster: resolving
                // per keystroke would put a request on the wire for every character typed.
                view.Editor.Text = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n";
                Settle();

                Assert.Equal(1, source.RequestCount);
            },
            CancellationToken.None);

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

                // By name, not by content: since KON-427 the button carries an icon and a label rather
                // than the bare string "Save".
                var save = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SaveButton");
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

                // The close button is inside the tab template, so it has no name of its own — its
                // tooltip is both its accessible name (DesignSystem.md §Accessibility) and the only
                // stable handle on it from here.
                var close = view.GetVisualDescendants().OfType<Button>()
                    .First(b => Equals(ToolTip.GetTip(b), "Close tab"));
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Empty(vm.OpenTabs);
            },
            CancellationToken.None);

    /// <summary>
    /// The recent list is the one way into a workspace that does not go through the folder picker
    /// (KON-434) — which makes it the only half of "open a folder" this suite can actually drive. It has
    /// to end where the picker ends: a workspace on the page, and the plugin told about it, or the git
    /// badges and the plan page would follow a folder nobody opened.
    /// </summary>
    [Fact]
    public Task Clicking_a_recent_folder_opens_it_as_the_workspace() =>
        headless.Session.Dispatch(
            () =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(_root, "platform-manifests")).FullName;
                File.WriteAllText(Path.Combine(folder, "deployment.yaml"), "kind: Deployment\n");

                // No DataContext: this is the empty state, which is where the list lives.
                var view = new WorkspaceView { Recent = [new RecentWorkspace(folder, IsKustomizeProject: false)] };
                var window = new Window { Width = 800, Height = 600, Content = view };
                window.Show();
                Settle();

                WorkspaceViewModel? announced = null;
                view.WorkspaceOpened += (_, workspace) => announced = workspace;

                var row = view.GetVisualDescendants().OfType<Button>()
                    .First(b => Equals(ToolTip.GetTip(b), folder));
                row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();

                var opened = Assert.IsType<WorkspaceViewModel>(view.DataContext);
                Assert.Equal(folder, opened.Workspace.RootPath);
                Assert.Same(opened, announced);
                Assert.Contains(opened.Rows, entry => entry.Name == "deployment.yaml");
            },
            CancellationToken.None);

    /// <summary>
    /// A clone has to end where the picker and the recent list end (KON-436): the folder on the page as
    /// the workspace, and the plugin told about it — otherwise git, Plan &amp; apply and Source control
    /// would follow a folder nobody opened. The clone itself is faked; what is under test is that its
    /// success turns into an opened workspace, which is the seam nothing else covers.
    /// </summary>
    [Fact]
    public Task Cloning_a_repository_opens_it_as_the_workspace() =>
        headless.Session.Dispatch(
            async () =>
            {
                // The fake runner starts no process, so the folder git would have created is put there
                // first — this test is about what happens after a clone reports success.
                var folder = Directory.CreateDirectory(Path.Combine(_root, "platform-manifests")).FullName;
                File.WriteAllText(Path.Combine(folder, "deployment.yaml"), "kind: Deployment\n");

                var runner = new FakeToolRunner().Install(new ExternalTool("git", "git", ["--version"], []));
                var clone = new CloneViewModel(new GitCli(runner))
                {
                    Url = "https://github.com/your-org/platform-manifests.git",
                    ParentFolder = _root,
                };

                // No DataContext: cloning lives on the empty state, same as the recent list.
                var view = new WorkspaceView { Clone = clone };
                var window = new Window { Width = 800, Height = 600, Content = view };
                window.Show();
                Settle();

                // The form is folded away until asked for — the card is about opening a workspace, and
                // three fields on it by default would make cloning look like the normal way in.
                var panel = view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "ClonePanel");
                Assert.False(panel.IsVisible);

                var reveal = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CloneButton");
                Assert.True(reveal.IsVisible);
                reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();

                Assert.True(panel.IsVisible);

                WorkspaceViewModel? announced = null;
                view.WorkspaceOpened += (_, workspace) => announced = workspace;

                await clone.CloneCommand.ExecuteAsync(null);
                Settle();

                Assert.Null(clone.Error);
                var opened = Assert.IsType<WorkspaceViewModel>(view.DataContext);
                Assert.Equal(folder, opened.Workspace.RootPath);
                Assert.Same(opened, announced);
                Assert.Contains(opened.Rows, entry => entry.Name == "deployment.yaml");
            },
            CancellationToken.None);

    /// <summary>A page with nowhere to clone to does not offer to.</summary>
    [Fact]
    public Task The_clone_button_stays_off_the_card_when_no_clone_model_was_handed_in() =>
        headless.Session.Dispatch(
            () =>
            {
                var view = new WorkspaceView();
                var window = new Window { Width = 800, Height = 600, Content = view };
                window.Show();
                Settle();

                var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CloneButton");
                Assert.False(button.IsVisible);
            },
            CancellationToken.None);

    /// <summary>Nothing remembered means no "Recent" heading over an empty list.</summary>
    [Fact]
    public Task The_recent_list_stays_off_the_page_when_there_is_nothing_to_offer() =>
        headless.Session.Dispatch(
            () =>
            {
                var view = new WorkspaceView();
                var window = new Window { Width = 800, Height = 600, Content = view };
                window.Show();
                Settle();

                var panel = view.GetVisualDescendants().OfType<StackPanel>()
                    .First(p => p.Name == "RecentPanel");
                Assert.False(panel.IsVisible);

                view.Recent = [new RecentWorkspace(_root, IsKustomizeProject: false)];
                Settle();

                Assert.True(panel.IsVisible);
            },
            CancellationToken.None);
}
