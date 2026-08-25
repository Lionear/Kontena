using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Writing the Data tab's fields back to the cluster (KON-422).
/// <para>
/// The editor shipped without this: Apply set a message and sent nothing, so every edit died on the
/// way out of edit mode. What each test here asks is the same question — after Apply, does the
/// cluster hold it?
/// </para>
/// </summary>
public sealed class ConfigEditApplyTests
{
    private static async Task<(ClusterConfigDetailViewModel Detail, FakeClusterEngine Cluster)> EditingAsync(
        string secret = "postgres-credentials")
    {
        var cluster = new FakeClusterEngine();
        var page = new ClusterSecretsViewModel(cluster, "app");
        await page.LoadAsync();

        var detail = new ClusterConfigDetailViewModel(cluster, page.Items.Single(r => r.Name == secret));
        for (var i = 0; i < 100 && detail.PodsLoading; i++)
            await Task.Delay(5);

        await detail.BeginEditCommand.ExecuteAsync(null);
        return (detail, cluster);
    }

    private static async Task<IReadOnlyList<ConfigEntry>> LiveAsync(FakeClusterEngine cluster, string name = "postgres-credentials") =>
        await cluster.GetConfigDataAsync(new ResourceRef(GroupVersionKind.Secret, "app", name));

    [Fact]
    public async Task A_changed_value_reaches_the_cluster()
    {
        var (detail, cluster) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated";

        await detail.ApplyCommand.ExecuteAsync(null);

        Assert.False(detail.StatusIsError);
        Assert.StartsWith("Applied ·", detail.Status, StringComparison.Ordinal);

        var live = await LiveAsync(cluster);
        Assert.Equal("9f2c-rotated", live.Single(e => e.Key == "password").Text);

        // The key nobody touched travels too, unharmed — Apply sends the whole object.
        Assert.Equal("postgres", live.Single(e => e.Key == "username").Text);
    }

    [Fact]
    public async Task An_added_key_reaches_the_cluster()
    {
        var (detail, cluster) = await EditingAsync();
        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "PGSSLMODE";
        detail.Keys[^1].Value = "verify-full";

        await detail.ApplyCommand.ExecuteAsync(null);

        var live = await LiveAsync(cluster);
        Assert.Equal(["PGSSLMODE", "password", "username"], live.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal("verify-full", live.Single(e => e.Key == "PGSSLMODE").Text);
    }

    /// <summary>
    /// Removal is the edit a merge would swallow: leave the key out of the document and a
    /// field-by-field merge puts the live one back, so the one edit that cannot be undone would be
    /// the one that silently did nothing.
    /// </summary>
    [Fact]
    public async Task A_removed_key_is_gone_from_the_cluster()
    {
        var (detail, cluster) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").RemoveCommand.Execute(null);

        await detail.ApplyCommand.ExecuteAsync(null);

        var live = await LiveAsync(cluster);
        Assert.Equal(["username"], live.Select(e => e.Key));
    }

    [Fact]
    public async Task A_renamed_key_moves_rather_than_multiplies()
    {
        var (detail, cluster) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Name = "PGPASSWORD";

        await detail.ApplyCommand.ExecuteAsync(null);

        var live = await LiveAsync(cluster);
        Assert.Equal(["PGPASSWORD", "username"], live.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal("s3cr3t-but-not-really", live.Single(e => e.Key == "PGPASSWORD").Text);
    }

    /// <summary>
    /// A certificate cannot be a field, and an editor that re-encoded it from what it managed to
    /// render would write a broken one. Editing the key beside it must leave it byte for byte.
    /// </summary>
    [Fact]
    public async Task A_binary_key_survives_an_edit_to_its_neighbour()
    {
        var (detail, cluster) = await EditingAsync("app-tls");
        var before = await LiveAsync(cluster, "app-tls");

        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "ca.crt.note";
        detail.Keys[^1].Value = "rotated by hand";

        await detail.ApplyCommand.ExecuteAsync(null);

        var after = await LiveAsync(cluster, "app-tls");
        foreach (var key in (string[])["tls.crt", "tls.key"])
        {
            Assert.Equal(before.Single(e => e.Key == key).Base64, after.Single(e => e.Key == key).Base64);
            Assert.Null(after.Single(e => e.Key == key).Text);
        }
    }

    /// <summary>
    /// Everything the editor never showed has to come back — this is why the data block is replaced
    /// in the live manifest rather than a fresh document being built from the fields.
    /// </summary>
    [Fact]
    public async Task The_secrets_type_survives_the_write()
    {
        var (detail, cluster) = await EditingAsync("app-tls");
        detail.Keys[0].Name = "tls.crt";

        var manifest = await cluster.GetManifestAsync(new ResourceRef(GroupVersionKind.Secret, "app", "app-tls"));
        Assert.Contains("type: kubernetes.io/tls", manifest, StringComparison.Ordinal);

        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "extra";
        detail.Keys[^1].Value = "x";
        await detail.ApplyCommand.ExecuteAsync(null);

        var after = await cluster.GetManifestAsync(new ResourceRef(GroupVersionKind.Secret, "app", "app-tls"));
        Assert.Contains("type: kubernetes.io/tls", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_reports_without_writing()
    {
        var (detail, cluster) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated";

        await detail.CheckCommand.ExecuteAsync(null);

        Assert.StartsWith("Would change ·", detail.Status, StringComparison.Ordinal);
        Assert.Equal("s3cr3t-but-not-really", (await LiveAsync(cluster)).Single(e => e.Key == "password").Text);

        // Still editing, still dirty: a dry-run answers a question, it does not finish anything.
        Assert.True(detail.IsEditing);
        Assert.True(detail.CanApply);
    }

    /// <summary>
    /// The bug this whole ticket came from: an applied edit used to be thrown away by the one
    /// button that leaves edit mode.
    /// </summary>
    [Fact]
    public async Task Leaving_the_tab_after_an_apply_keeps_the_edit()
    {
        var (detail, cluster) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated";
        await detail.ApplyCommand.ExecuteAsync(null);

        // Apply ends the edit itself, so there is nothing left to cancel — and pressing it anyway
        // cannot reach back past the write.
        Assert.False(detail.IsEditing);
        detail.CancelEditCommand.Execute(null);

        Assert.Equal("9f2c-rotated", (await LiveAsync(cluster)).Single(e => e.Key == "password").Text);
    }

    [Fact]
    public async Task The_fields_become_a_reading_of_what_was_written()
    {
        var (detail, _) = await EditingAsync();
        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "PGSSLMODE";
        detail.Keys[^1].Value = "verify-full";

        await detail.ApplyCommand.ExecuteAsync(null);

        Assert.False(detail.IsEditing);
        Assert.False(detail.IsDirty);
        Assert.Equal("3 keys", detail.KeyCountText);
        Assert.Equal(["PGSSLMODE", "password", "username"], detail.Keys.Select(k => k.Name).Order(StringComparer.Ordinal));

        // Read again, so a secret is not left sitting in the page after the write.
        Assert.All(detail.Keys, key => Assert.Null(key.Value));
        Assert.Equal("11 B", detail.Keys.Single(k => k.Name == "PGSSLMODE").Size);
    }

    [Fact]
    public async Task A_key_without_a_name_is_refused_here_rather_than_by_the_cluster()
    {
        var (detail, cluster) = await EditingAsync();
        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Value = "orphan";

        await detail.ApplyCommand.ExecuteAsync(null);

        Assert.True(detail.StatusIsError);
        Assert.Equal("A key needs a name.", detail.Status);
        Assert.Equal(2, (await LiveAsync(cluster)).Count);
    }

    [Fact]
    public async Task Two_keys_with_one_name_are_refused()
    {
        var (detail, cluster) = await EditingAsync();
        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "username";
        detail.Keys[^1].Value = "second";

        await detail.ApplyCommand.ExecuteAsync(null);

        Assert.True(detail.StatusIsError);
        Assert.Contains("two keys called username", detail.Status, StringComparison.Ordinal);
        Assert.Equal(2, (await LiveAsync(cluster)).Count);
    }

    [Fact]
    public async Task Applying_the_same_values_twice_is_reported_as_no_change()
    {
        var (detail, _) = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated";
        await detail.ApplyCommand.ExecuteAsync(null);

        await detail.BeginEditCommand.ExecuteAsync(null);
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated-and-back";
        detail.Keys.First(k => k.Name == "password").Value = "9f2c-rotated";

        // Nothing differs from the cluster, so there is nothing to send.
        Assert.False(detail.CanApply);
    }
}
