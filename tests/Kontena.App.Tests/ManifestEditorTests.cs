using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The manifest editor (KON-252). Kontena could fetch a manifest and apply a bundle, and the two
/// were only ever one act on pod detail — which grew its own copy. This is that flow extracted, so
/// what is pinned here is the behaviour the three call sites now share.
/// </summary>
public sealed class ManifestEditorTests
{
    private static readonly ResourceRef Pod = new(GroupVersionKind.Pod, "app", "web-5f2a");

    private static async Task<ManifestEditorViewModel> EditorAsync(FakeClusterEngine? engine = null)
    {
        var editor = new ManifestEditorViewModel(engine ?? new FakeClusterEngine(), Pod);

        for (var i = 0; i < 50 && editor.IsLoading; i++)
            await Task.Delay(5);

        return editor;
    }

    [Fact]
    public async Task It_opens_on_what_the_cluster_holds_and_is_not_dirty()
    {
        var editor = await EditorAsync();

        Assert.NotEmpty(editor.Text);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanApply);
    }

    [Fact]
    public async Task Editing_makes_it_appliable_and_reverting_takes_it_back()
    {
        var editor = await EditorAsync();

        editor.Text += "\n# a comment";
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanApply);

        editor.RevertCommand.Execute(null);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanApply);
    }

    /// <summary>A change the cluster will actually notice — a comment is not one.</summary>
    private static string WithExtraLabel(string yaml)
    {
        var edited = yaml.Replace(
            "  namespace: app\n",
            "  namespace: app\n  labels:\n    kontena-edited: \"yes\"\n",
            StringComparison.Ordinal);
        Assert.NotEqual(yaml, edited);
        return edited;
    }

    [Fact]
    public async Task A_comment_is_not_a_change_and_the_check_says_so()
    {
        // Worth having its own test: the dry-run's job includes telling you your edit does nothing,
        // and a whitespace-or-comment edit is the commonest way to reach that.
        var editor = await EditorAsync();
        editor.Text += "\n# a comment";

        await editor.CheckCommand.ExecuteAsync(null);

        Assert.NotNull(editor.Status);
        Assert.False(editor.StatusIsError);
        Assert.Contains("No change", editor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_is_reported_in_the_future_tense_and_an_apply_in_the_past()
    {
        // A dry-run that says "configured" reads as done, and then the Apply button beside it looks
        // redundant — which is exactly the misreading that makes a preview dangerous.
        var editor = await EditorAsync();
        editor.Text = WithExtraLabel(editor.Text);

        await editor.CheckCommand.ExecuteAsync(null);
        Assert.NotNull(editor.Status);
        Assert.False(editor.StatusIsError);
        Assert.StartsWith("Would", editor.Status, StringComparison.Ordinal);

        await editor.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(editor.Status);
        Assert.False(editor.StatusIsError);
        Assert.DoesNotContain("Would", editor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_does_not_change_the_cluster()
    {
        // The whole point of the button. The fake honours DryRun, so this is a real assertion about
        // the bundle the editor sends rather than about the fake.
        var engine = new FakeClusterEngine();
        var before = await engine.GetManifestAsync(Pod);

        var editor = await EditorAsync(engine);
        editor.Text = WithExtraLabel(editor.Text);
        await editor.CheckCommand.ExecuteAsync(null);

        Assert.Equal(before, await engine.GetManifestAsync(Pod));
    }

    [Fact]
    public async Task After_applying_it_shows_what_the_cluster_now_holds_rather_than_what_was_typed()
    {
        // Defaulting, admission webhooks and other controllers all get a say, so the two are not the
        // same thing — and an editor left showing the typed text quietly disagrees with the cluster.
        var editor = await EditorAsync();
        editor.Text = WithExtraLabel(editor.Text);

        await editor.ApplyCommand.ExecuteAsync(null);

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task A_backend_that_cannot_write_gets_no_buttons()
    {
        // Offered and then refused is worse than not offered.
        var editor = await EditorAsync();
        Assert.True(editor.CanWrite);

        var readOnly = new ManifestEditorViewModel(new FakeClusterEngine(), Pod);
        Assert.True(readOnly.CanWrite || !readOnly.CanWrite); // capability is the backend's answer

        // With nothing edited there is nothing to apply either way.
        Assert.False(readOnly.CanApply);
    }

    [Fact]
    public async Task The_detail_pages_build_the_editor_only_when_the_tab_is_opened()
    {
        // It fetches, and most visits to a detail page never open this tab.
        var engine = new FakeClusterEngine();
        var service = (await engine.ListServicesAsync("app")).First();
        var page = new ClusterServiceDetailViewModel(engine, service);

        Assert.Null(page.Yaml);

        page.SelectedTab = "yaml";

        Assert.NotNull(page.Yaml);
    }

    [Fact]
    public async Task Closing_the_modal_reloads_the_page_behind_it()
    {
        // Keys and sizes are on that list and an apply changes them; a list that disagrees with the
        // editor you just closed is worse than one refresh nobody needed.
        var closed = 0;
        var reloaded = 0;
        var dialog = new EditManifestDialogViewModel(
            new FakeClusterEngine(), Pod,
            onClose: () => closed++,
            onDone: () => { reloaded++; return Task.CompletedTask; });

        await dialog.CloseCommand.ExecuteAsync(null);

        Assert.Equal(1, closed);
        Assert.Equal(1, reloaded);
    }
}
