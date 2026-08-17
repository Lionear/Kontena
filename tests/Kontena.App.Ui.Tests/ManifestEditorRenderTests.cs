using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The manifest editor, and the cap that is gone with it (KON-382).
/// <para>
/// The bundle that motivated all of this — <c>kube-prometheus-stack</c>, 5.2 MB across 82,000 lines
/// — used to reach the page clipped to 512 KB, because a <c>TextBox</c> lays out every line it is
/// given. What is worth a test here is not the milliseconds, which headless Avalonia measures with a
/// stub glyph anyway: it is that the whole bundle arrives, that edits come back out, and that a big
/// one is parsed off the thread that draws.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ManifestEditorRenderTests(HeadlessSessionFixture headless)
{
    /// <summary>Comfortably over the editor's off-thread threshold, in the shape of a manifest.</summary>
    private static string BigBundle(int lines = 20_000) =>
        string.Concat(Enumerable.Range(0, lines).Select(i =>
            $"            - name: probe-{i} # padded out to something like a real line length\n"));

    private static ManifestEditor Show(string text)
    {
        var editor = new ManifestEditor { Text = text };
        var window = new Window { Content = editor, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return editor;
    }

    /// <summary>Drives the dispatcher until a background parse has landed, rather than guessing at a delay.</summary>
    private static void PumpUntil(Func<bool> settled, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!settled() && waited.ElapsedMilliseconds < 30_000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(settled(), $"timed out waiting for {what}");
    }

    [Fact]
    public Task A_small_manifest_is_there_the_moment_it_is_set() =>
        headless.Session.Dispatch(() =>
        {
            const string yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: a\n";
            var editor = Show(yaml);

            // Small enough to build inline: no frame of the previous document, no async settling.
            Assert.Equal(yaml, editor.Editor.Document.Text);
        }, CancellationToken.None);

    [Fact]
    public Task A_big_bundle_arrives_whole_instead_of_clipped() =>
        headless.Session.Dispatch(() =>
        {
            var yaml = BigBundle();
            var editor = Show(yaml);

            PumpUntil(() => editor.Editor.Document.TextLength == yaml.Length, "the bundle to be parsed");

            Assert.Equal(yaml, editor.Editor.Document.Text);
            Assert.Equal(20_000, editor.Editor.Document.LineCount - 1); // The trailing newline opens one more.
        }, CancellationToken.None);

    /// <summary>
    /// The half of the work that does not have to happen on the thread that draws, does not: setting
    /// a big bundle hands back before the document exists. This is the lesson KON-381 paid for on the
    /// apply itself, applied to the render.
    /// </summary>
    [Fact]
    public Task A_big_bundle_is_parsed_off_the_UI_thread() =>
        headless.Session.Dispatch(() =>
        {
            var editor = Show("apiVersion: v1\n");
            var yaml = BigBundle();

            editor.Text = yaml;

            // Still the old document: the setter returned before the parse finished.
            Assert.NotEqual(yaml.Length, editor.Editor.Document.TextLength);

            PumpUntil(() => editor.Editor.Document.TextLength == yaml.Length, "the bundle to be parsed");
        }, CancellationToken.None);

    /// <summary>
    /// Two renders in a row, the first one bigger: the slower parse must not land on top of the
    /// newer bundle and leave the page showing something nobody asked for.
    /// </summary>
    [Fact]
    public Task The_newest_bundle_wins_however_slowly_an_older_one_parses() =>
        headless.Session.Dispatch(() =>
        {
            var editor = Show("apiVersion: v1\n");

            editor.Text = BigBundle(40_000);
            editor.Text = "kind: ConfigMap\n";

            PumpUntil(() => editor.Editor.Document.Text == "kind: ConfigMap\n", "the newer bundle");

            // Give the older parse every chance to arrive late and overwrite it.
            for (var i = 0; i < 100; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.Equal("kind: ConfigMap\n", editor.Editor.Document.Text);
        }, CancellationToken.None);

    [Fact]
    public Task Typing_in_the_editor_writes_through_to_the_bundle() =>
        headless.Session.Dispatch(() =>
        {
            var editor = Show("kind: ConfigMap\n");

            editor.Editor.Document.Insert(editor.Editor.Document.TextLength, "metadata:\n  name: a\n");

            Assert.Equal("kind: ConfigMap\nmetadata:\n  name: a\n", editor.Text);
        }, CancellationToken.None);

    /// <summary>The page is where it has to work: bound to the view-model, both directions.</summary>
    [Fact]
    public Task The_apply_page_shows_its_bundle_and_takes_edits_back() =>
        headless.Session.Dispatch(() =>
        {
            var vm = new ApplyManifestViewModel(new FakeClusterEngine(), "kind-test")
            {
                YamlText = "kind: ConfigMap\n",
            };
            var view = new ApplyManifestView { DataContext = vm };
            var window = new Window { Content = view, Width = 1400, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var editor = view.GetVisualDescendants().OfType<ManifestEditor>().Single();

            Assert.Equal("kind: ConfigMap\n", editor.Editor.Document.Text);

            // The editor must not be squeezed out by the panels stacked above it.
            Assert.True(editor.Bounds.Height > 400, $"editor collapsed to {editor.Bounds.Height}px");

            editor.Editor.Document.Insert(editor.Editor.Document.TextLength, "metadata:\n  name: a\n");
            Assert.Equal("kind: ConfigMap\nmetadata:\n  name: a\n", vm.YamlText);
        }, CancellationToken.None);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Read_only_reaches_the_editor(bool readOnly) =>
        headless.Session.Dispatch(() =>
        {
            var editor = Show("kind: ConfigMap\n");

            editor.IsReadOnly = readOnly;

            Assert.Equal(readOnly, editor.Editor.IsReadOnly);
        }, CancellationToken.None);

    /// <summary>
    /// A resource's own YAML gets the same editor as a bundle does (KON-383). It used to be a plain
    /// text box, with the same ceiling: a CRD's manifest is a fourteen-thousand-line document.
    /// </summary>
    [Fact]
    public Task A_resources_yaml_pane_shows_what_the_cluster_holds() =>
        headless.Session.Dispatch(async () =>
        {
            var vm = await LoadedEditorAsync(new FakeClusterEngine());
            var editor = Editor(new ManifestEditorView { DataContext = vm });

            Assert.Equal(vm.Text, editor.Editor.Document.Text);
            Assert.False(editor.IsReadOnly);

            editor.Editor.Document.Insert(0, "# touched\n");
            Assert.StartsWith("# touched\n", vm.Text, StringComparison.Ordinal);
            Assert.True(vm.IsDirty);
        }, CancellationToken.None);

    /// <summary>A backend that cannot apply still shows the manifest; it just will not take it back.</summary>
    [Fact]
    public Task A_backend_that_cannot_apply_gets_a_read_only_pane() =>
        headless.Session.Dispatch(async () =>
        {
            var vm = await LoadedEditorAsync(new FakeClusterEngine { CanApply = false });
            var editor = Editor(new ManifestEditorView { DataContext = vm });

            Assert.NotEmpty(editor.Editor.Document.Text);
            Assert.True(editor.IsReadOnly);
            Assert.True(editor.Editor.IsReadOnly);
        }, CancellationToken.None);

    /// <summary>Pod detail keeps its own copy of the YAML tab, so it needs its own guard.</summary>
    [Fact]
    public Task Pod_detail_uses_the_same_editor_for_its_yaml_tab() =>
        headless.Session.Dispatch(() =>
        {
            var view = new ClusterPodDetailView();
            var window = new Window { Content = view, Width = 1400, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view.GetVisualDescendants().OfType<ManifestEditor>());
        }, CancellationToken.None);

    private static async Task<ManifestEditorViewModel> LoadedEditorAsync(FakeClusterEngine engine)
    {
        var vm = new ManifestEditorViewModel(engine, new ResourceRef(GroupVersionKind.Pod, "app", "web-5f2a"));

        for (var i = 0; i < 100 && vm.IsLoading; i++)
            await Task.Delay(5);

        Assert.False(vm.IsLoading);

        return vm;
    }

    private static ManifestEditor Editor(ManifestEditorView view)
    {
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return view.GetVisualDescendants().OfType<ManifestEditor>().Single();
    }
}
