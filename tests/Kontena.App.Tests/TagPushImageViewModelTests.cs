using Kontena.App.ViewModels;
using Kontena.Engines.Fakes;
using Kontena.Sdk;

namespace Kontena.App.Tests;

/// <summary>
/// Tag and push, from the row action to the engine (KON-387).
/// <para>
/// The case worth guarding is the one that is easy to get wrong: a push under a name the image does not
/// carry yet. The dialog has to tag first, and it has to push the <em>target</em> — a push that goes out
/// under the old name uploads the right bytes to the wrong repository, and nothing later would flag it.
/// </para>
/// </summary>
public sealed class TagPushImageViewModelTests
{
    private static async Task<(FakeEngine Engine, ImageRowViewModel Row)> ImageAsync()
    {
        var engine = new FakeEngine();
        var page = new ImagesViewModel(engine);
        await page.LoadAsync();
        return (engine, page.Items[0]);
    }

    private static TagPushImageViewModel Dialog(FakeEngine engine, ImageRowViewModel row) =>
        new(engine, row.Id, row.Reference, onClose: () => { }, onChanged: () => Task.CompletedTask);

    [Fact]
    public async Task The_row_action_hands_the_shell_the_image_it_was_clicked_on()
    {
        var engine = new FakeEngine();
        ImageRowViewModel? asked = null;
        var page = new ImagesViewModel(engine) { RequestTagPushImage = row => asked = row };
        await page.LoadAsync();

        page.Items[0].TagAndPushCommand.Execute(null);

        Assert.Same(page.Items[0], asked);
    }

    [Fact]
    public async Task The_target_starts_as_the_name_the_image_already_has()
    {
        var (engine, row) = await ImageAsync();

        Assert.Equal(row.Reference, Dialog(engine, row).Target);
    }

    [Fact]
    public async Task Tagging_adds_the_new_name_and_leaves_the_old_one()
    {
        var (engine, row) = await ImageAsync();
        var dialog = Dialog(engine, row);
        dialog.Target = "ghcr.io/me/app:1.2";

        await dialog.TagCommand.ExecuteAsync(null);

        var images = await engine.ListImagesAsync();
        Assert.Contains(images, i => i.Repository == "ghcr.io/me/app" && i.Tag == "1.2");
        Assert.Contains(images, i => $"{i.Repository}:{i.Tag}" == row.Reference);
        Assert.True(dialog.IsDone);
        Assert.Null(dialog.Error);
    }

    [Fact]
    public async Task Pushing_a_new_name_tags_it_first_and_pushes_that_name()
    {
        var (engine, row) = await ImageAsync();
        var dialog = Dialog(engine, row);
        dialog.Target = "ghcr.io/me/app:1.2";

        await dialog.PushCommand.ExecuteAsync(null);

        Assert.Equal(["ghcr.io/me/app:1.2"], engine.PushedReferences);
        Assert.Contains(await engine.ListImagesAsync(), i => i.Repository == "ghcr.io/me/app" && i.Tag == "1.2");
        Assert.True(dialog.IsDone);
        Assert.Equal("Push complete", dialog.Status);
    }

    [Fact]
    public async Task Pushing_the_name_it_already_has_tags_nothing()
    {
        var (engine, row) = await ImageAsync();
        var before = (await engine.ListImagesAsync()).Count;
        var dialog = Dialog(engine, row);

        await dialog.PushCommand.ExecuteAsync(null);

        Assert.Equal([row.Reference], engine.PushedReferences);
        Assert.Equal(before, (await engine.ListImagesAsync()).Count);
        Assert.True(dialog.IsDone);
    }

    [Fact]
    public async Task Whitespace_around_the_target_does_not_reach_the_engine()
    {
        var (engine, row) = await ImageAsync();
        var dialog = Dialog(engine, row);
        dialog.Target = "  ghcr.io/me/app:1.2  ";

        await dialog.PushCommand.ExecuteAsync(null);

        Assert.Equal(["ghcr.io/me/app:1.2"], engine.PushedReferences);
    }

    /// <summary>
    /// A refusal is where a push flow earns its keep: "unauthorized" names no fix, and the account that
    /// would have fixed it is stored in a place the error never mentions.
    /// </summary>
    [Fact]
    public async Task A_refused_push_says_which_registry_refused_and_where_a_login_lives()
    {
        var (engine, row) = await ImageAsync();
        engine.FailOn = nameof(IContainerEngine.PushImageAsync);
        var dialog = Dialog(engine, row);
        dialog.Target = "ghcr.io/me/app:1.2";

        await dialog.PushCommand.ExecuteAsync(null);

        Assert.False(dialog.IsDone);
        Assert.NotNull(dialog.Error);
        Assert.Contains("ghcr.io", dialog.Error, StringComparison.Ordinal);
        Assert.Contains("Registries", dialog.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_a_push_would_reach_is_shown_before_it_runs()
    {
        var (engine, row) = await ImageAsync();
        var dialog = Dialog(engine, row);

        dialog.Target = "ghcr.io/me/app:1.2";
        Assert.Equal("ghcr.io", dialog.TargetRegistry);

        // An unqualified name is Docker Hub, not a registry called "me".
        dialog.Target = "me/app:1.2";
        Assert.Equal("docker.io", dialog.TargetRegistry);
    }

    [Fact]
    public async Task An_empty_target_is_not_something_either_button_will_run()
    {
        var (engine, row) = await ImageAsync();
        var dialog = Dialog(engine, row);

        dialog.Target = "   ";

        Assert.False(dialog.CanApply);
    }
}
