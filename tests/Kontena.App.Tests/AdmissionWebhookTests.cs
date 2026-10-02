using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The admission webhooks page (KON-478). What is pinned is the part that answers "why won't my apply
/// go through": what a webhook is called for, and which way its own outage goes.
/// </summary>
public sealed class AdmissionWebhookTests
{
    [Fact]
    public void A_rule_says_the_operations_and_the_resources_they_apply_to()
    {
        var rule = new WebhookRule { Operations = ["CREATE", "UPDATE"], ApiGroups = ["", "apps"], Resources = ["deployments", "pods"] };

        // Core resources stay bare, the way kubectl prints them; the rest carry their group.
        Assert.Equal("CREATE, UPDATE on deployments, pods, deployments.apps, pods.apps", AdmissionWebhookRow.Describe(rule));
    }

    [Fact]
    public void A_catch_all_rule_is_said_in_words()
    {
        var rule = new WebhookRule { Operations = ["*"], ApiGroups = ["*"], Resources = ["*"] };

        Assert.Equal("Any operation on every resource", AdmissionWebhookRow.Describe(rule));
    }

    [Fact]
    public void Every_resource_of_one_group_is_named_by_the_group()
    {
        var rule = new WebhookRule { Operations = ["CREATE"], ApiGroups = ["cert-manager.io"], Resources = ["*/*"] };

        Assert.Equal("CREATE on everything in cert-manager.io", AdmissionWebhookRow.Describe(rule));
    }

    [Fact]
    public void Fail_is_explained_as_what_happens_to_your_request()
    {
        var row = new AdmissionWebhookRow(new AdmissionWebhook
        {
            Name = "validate.kyverno.svc", Configuration = "kyverno", Target = "kyverno/kyverno-svc", TimeoutSeconds = 10,
        });

        Assert.True(row.FailsClosed);
        Assert.Equal("Fail", row.FailurePolicy);
        Assert.Contains("rejected", row.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("kyverno/kyverno-svc", row.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_is_not_marked()
    {
        var row = new AdmissionWebhookRow(new AdmissionWebhook
        {
            Name = "webhook.cert-manager.io", Configuration = "cert-manager-webhook",
            FailurePolicy = WebhookFailurePolicy.Ignore,
        });

        Assert.False(row.FailsClosed);
        Assert.Contains("unchecked", row.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_webhook_without_rules_says_it_is_never_called()
    {
        var row = new AdmissionWebhookRow(new AdmissionWebhook { Name = "idle", Configuration = "cfg" });

        Assert.Equal("No rules — never called", row.Rules);
    }

    [Fact]
    public async Task The_page_lists_every_webhook_and_searches_by_configuration()
    {
        using var vm = new ClusterWebhooksViewModel(new FakeClusterEngine());
        await vm.LoadAsync();

        Assert.Equal(3, vm.Items.Count);
        Assert.Contains(vm.Items, r => r.Kind == "Mutating");

        vm.SearchText = "cert-manager";

        Assert.Equal("webhook.cert-manager.io", Assert.Single(vm.Items).Name);
    }
}
