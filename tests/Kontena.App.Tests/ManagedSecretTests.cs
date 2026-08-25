using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// A Secret somebody else keeps up to date (KON-422).
/// <para>
/// The External Secrets Operator reconciles its target Secret from an ExternalSecret. Kubernetes
/// will happily take a hand-written change to one — and then ESO puts its own value back, on its
/// own schedule. That is worse than a refusal: the person who typed it watched it save.
/// </para>
/// </summary>
public sealed class ManagedSecretTests
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

    /// <summary>
    /// The label, not the ownerReference. ESO sets an owner only under <c>creationPolicy: Owner</c>,
    /// and goes on reconciling under Orphan, Merge and CreateOrMerge — where there is none.
    /// </summary>
    [Fact]
    public void The_label_eso_puts_on_every_secret_it_manages_is_what_counts()
    {
        Assert.Equal("reconcile.external-secrets.io/managed", ManagedSecrets.ExternalSecretsLabel);

        Assert.True(ManagedSecrets.IsExternallyManaged(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reconcile.external-secrets.io/managed"] = "true" }));
    }

    [Fact]
    public void A_secret_nobody_reconciles_is_not_managed()
    {
        Assert.False(ManagedSecrets.IsExternallyManaged(null));
        Assert.False(ManagedSecrets.IsExternallyManaged(new Dictionary<string, string>(StringComparer.Ordinal)));

        // An unrelated label from the same project is not a claim on this object.
        Assert.False(ManagedSecrets.IsExternallyManaged(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["app.kubernetes.io/managed-by"] = "argocd" }));
    }

    /// <summary>
    /// The value is read, not just the key: "false" is somebody saying it is not managed, and a
    /// check that treats any value as yes makes the label impossible to turn off.
    /// </summary>
    [Fact]
    public void The_label_set_to_false_means_false()
    {
        Assert.False(ManagedSecrets.IsExternallyManaged(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reconcile.external-secrets.io/managed"] = "false" }));
    }

    [Fact]
    public async Task A_managed_secret_does_not_offer_to_be_edited()
    {
        var detail = await DetailAsync("stripe-api");

        Assert.True(detail.IsExternallyManaged);
        Assert.False(detail.CanEdit);

        // Said plainly, and as a fact rather than a fault: nothing is wrong with this Secret.
        Assert.Contains("External Secrets Operator", detail.ExternallyManagedNotice, StringComparison.Ordinal);
        Assert.Contains("ExternalSecret", detail.ExternallyManagedNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("error", detail.ExternallyManagedNotice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warning", detail.ExternallyManagedNotice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reading it is untouched — this is about writing, and only about writing.</summary>
    [Fact]
    public async Task A_managed_secret_still_shows_its_keys_and_reveals_them()
    {
        var detail = await DetailAsync("stripe-api");

        Assert.Equal(["secret-key", "webhook-secret"], detail.Keys.Select(k => k.Name).Order(StringComparer.Ordinal));

        var row = detail.Keys.First();
        await row.ToggleCommand.ExecuteAsync(null);

        Assert.True(row.IsRevealed);
        Assert.Equal("sk_live_51Mx8Qp2eZvKYlo2C0000", row.Value);
    }

    [Fact]
    public async Task An_ordinary_secret_is_still_editable()
    {
        var detail = await DetailAsync("postgres-credentials");

        Assert.False(detail.IsExternallyManaged);
        Assert.True(detail.CanEdit);
    }
}
