using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>The admission webhook mappers (KON-478).</summary>
public class K8sWebhookTests
{
    [Fact]
    public void A_failure_policy_the_cluster_did_not_state_is_Fail()
    {
        // v1's default, and the direction that blocks applies. Guessing Ignore would show the webhook
        // that is rejecting everything as the harmless kind.
        var w = Assert.Single(K8sMap.ToWebhooks(new V1ValidatingWebhookConfiguration
        {
            Metadata = new V1ObjectMeta { Name = "cfg" },
            Webhooks = [new V1ValidatingWebhook { Name = "v.example.io" }],
        }));

        Assert.Equal(WebhookFailurePolicy.Fail, w.FailurePolicy);
        Assert.Equal(10, w.TimeoutSeconds);
    }

    [Fact]
    public void Every_webhook_of_a_configuration_is_its_own_entry()
    {
        var hooks = K8sMap.ToWebhooks(new V1MutatingWebhookConfiguration
        {
            Metadata = new V1ObjectMeta { Name = "kyverno-resource-mutating-webhook-cfg" },
            Webhooks =
            [
                new V1MutatingWebhook
                {
                    Name = "a.kyverno.svc", FailurePolicy = "Ignore", TimeoutSeconds = 3,
                    ClientConfig = new Admissionregistrationv1WebhookClientConfig
                    {
                        Service = new Admissionregistrationv1ServiceReference { Name = "kyverno-svc", NamespaceProperty = "kyverno" },
                    },
                    Rules =
                    [
                        new V1RuleWithOperations { Operations = ["CREATE"], ApiGroups = ["apps"], Resources = ["deployments"] },
                    ],
                },
                new V1MutatingWebhook
                {
                    Name = "b.kyverno.svc",
                    ClientConfig = new Admissionregistrationv1WebhookClientConfig { Url = "https://policy.example.io/mutate" },
                },
            ],
        }).ToList();

        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, h => Assert.Equal("kyverno-resource-mutating-webhook-cfg", h.Configuration));
        Assert.All(hooks, h => Assert.Equal(AdmissionWebhookKind.Mutating, h.Kind));

        Assert.Equal(WebhookFailurePolicy.Ignore, hooks[0].FailurePolicy);
        Assert.Equal(3, hooks[0].TimeoutSeconds);
        Assert.Equal("kyverno/kyverno-svc", hooks[0].Target);
        var rule = Assert.Single(hooks[0].Rules);
        Assert.Equal(["CREATE"], rule.Operations);
        Assert.Equal(["apps"], rule.ApiGroups);
        Assert.Equal(["deployments"], rule.Resources);

        // An external webhook is named by its URL.
        Assert.Equal("https://policy.example.io/mutate", hooks[1].Target);
    }
}
