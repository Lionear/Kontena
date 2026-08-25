using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Leaving the Data tab's field editor (KON-418).
/// <para>
/// Cancel was reported as losing an edit halfway. It does not: every case below shows it putting the
/// rows back exactly as the cluster has them. What did go wrong was the other half of the flow —
/// Apply said "Applied · Secret/x configured" while sending nothing, so the only way out of edit
/// mode was a Cancel that dropped the edit the message had just called saved.
/// </para>
/// </summary>
public sealed class ConfigEditExitTests
{
    private static async Task<ClusterConfigDetailViewModel> DetailAsync(string secret)
    {
        var cluster = new FakeClusterEngine();
        var page = new ClusterSecretsViewModel(cluster, "app");
        await page.LoadAsync();
        var detail = new ClusterConfigDetailViewModel(cluster, page.Items.Single(r => r.Name == secret));

        for (var i = 0; i < 100 && detail.PodsLoading; i++)
            await Task.Delay(5);

        return detail;
    }

    private static async Task<ClusterConfigDetailViewModel> EditingAsync(string secret = "postgres-credentials")
    {
        var detail = await DetailAsync(secret);
        await detail.BeginEditCommand.ExecuteAsync(null);
        return detail;
    }

    /// <summary>The state a secret's Data tab is in when nobody is editing it: keys, no values.</summary>
    private static void AssertReading(ClusterConfigDetailViewModel detail)
    {
        Assert.False(detail.IsEditing);
        Assert.False(detail.IsDirty);
        Assert.False(detail.CanApply);
        Assert.Equal(["password", "username"], detail.Keys.Select(k => k.Name).Order());
        Assert.Equal("2 keys", detail.KeyCountText);

        Assert.All(detail.Keys, key =>
        {
            // Dropped, not merely hidden — the same promise reading makes (KON-249).
            Assert.Null(key.Value);
            Assert.False(key.IsRevealed);
            Assert.False(key.IsEditing);
            Assert.False(key.IsNew);
            Assert.False(key.IsChanged);
        });
    }

    [Fact]
    public async Task Cancel_puts_a_changed_value_back()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";
        Assert.True(detail.CanApply);

        detail.CancelEditCommand.Execute(null);

        AssertReading(detail);
    }

    [Fact]
    public async Task Cancel_puts_a_renamed_key_back()
    {
        var detail = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").Name = "PGPASSWORD";

        detail.CancelEditCommand.Execute(null);

        AssertReading(detail);
    }

    [Fact]
    public async Task Cancel_drops_a_key_that_was_added()
    {
        var detail = await EditingAsync();
        detail.AddKeyCommand.Execute(null);
        detail.Keys[^1].Name = "PGSSLMODE";
        detail.Keys[^1].Value = "verify-full";
        Assert.Equal("3 keys", detail.KeyCountText);

        detail.CancelEditCommand.Execute(null);

        AssertReading(detail);
    }

    [Fact]
    public async Task Cancel_brings_back_a_key_that_was_removed()
    {
        var detail = await EditingAsync();
        detail.Keys.First(k => k.Name == "password").RemoveCommand.Execute(null);
        Assert.Single(detail.Keys);

        detail.CancelEditCommand.Execute(null);

        AssertReading(detail);
    }

    /// <summary>
    /// Editing twice has to start from the cluster both times. The rows survive a cancel, so a stale
    /// original on one of them would make the second edit's Apply button decide against a value
    /// nobody holds any more.
    /// </summary>
    [Fact]
    public async Task Editing_again_after_a_cancel_starts_clean()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";
        detail.CancelEditCommand.Execute(null);

        await detail.BeginEditCommand.ExecuteAsync(null);

        Assert.True(detail.IsEditing);
        Assert.False(detail.IsDirty);
        Assert.False(detail.CanApply);
        Assert.All(detail.Keys, key => Assert.False(key.IsChanged));
    }

    /// <summary>
    /// The reported bug, at its root: Apply must not report a write it did not do. Nothing here is
    /// wired to the cluster yet, and a page that says otherwise sends someone away believing a
    /// secret was rotated.
    /// </summary>
    [Fact]
    public async Task Apply_does_not_claim_a_write_it_did_not_make()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";

        detail.ApplyCommand.Execute(null);

        Assert.True(detail.StatusIsError);
        Assert.DoesNotContain("Applied", detail.Status, StringComparison.Ordinal);
        Assert.Contains("YAML tab", detail.Status, StringComparison.Ordinal);

        // And the edit is still in hand rather than sitting behind a confirmation.
        Assert.True(detail.IsEditing);
        Assert.True(detail.CanApply);
        Assert.Equal("9f2c-rotated", detail.Keys[0].Value);
    }

    [Fact]
    public async Task Check_does_not_claim_a_dry_run_it_did_not_make()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";

        detail.CheckCommand.Execute(null);

        Assert.True(detail.StatusIsError);
        Assert.DoesNotContain("Would change", detail.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A status describes one state of the fields. Typing again makes it a claim about a value that
    /// is no longer on screen, which is how "Applied ·" came to sit over an unsaved edit.
    /// </summary>
    [Fact]
    public async Task A_status_does_not_outlive_the_edit_it_described()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";
        detail.CheckCommand.Execute(null);
        Assert.NotNull(detail.Status);

        detail.Keys[0].Value = "9f2c-rotated-again";

        Assert.Null(detail.Status);
        Assert.False(detail.StatusIsError);
    }

    [Fact]
    public async Task Cancel_clears_the_status_too()
    {
        var detail = await EditingAsync();
        detail.Keys[0].Value = "9f2c-rotated";
        detail.ApplyCommand.Execute(null);

        detail.CancelEditCommand.Execute(null);

        Assert.Null(detail.Status);
        Assert.False(detail.StatusIsError);
        AssertReading(detail);
    }
}
