namespace Kontena.Sdk.Orchestration.Models;

/// <summary>Which of the two admission webhook kinds a webhook is declared under.</summary>
public enum AdmissionWebhookKind
{
    /// <summary>May change the object before it is stored.</summary>
    Mutating,

    /// <summary>May only accept or reject it.</summary>
    Validating,
}

/// <summary>What the API server does when a webhook cannot be reached or does not answer in time.</summary>
public enum WebhookFailurePolicy
{
    /// <summary>The request is rejected. Kubernetes' default in admissionregistration/v1.</summary>
    Fail,

    /// <summary>The request goes through as if the webhook had said yes.</summary>
    Ignore,
}

/// <summary>One rule of a webhook: which operations on which resources it is called for.</summary>
public sealed record WebhookRule
{
    /// <summary>CREATE, UPDATE, DELETE, CONNECT, or "*".</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>API groups, "" being core and "*" being every group.</summary>
    public IReadOnlyList<string> ApiGroups { get; init; } = [];

    /// <summary>Resources, subresources included ("pods/exec"); "*" is every resource.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];
}

/// <summary>
/// One webhook out of a Validating- or MutatingWebhookConfiguration (KON-478). A configuration can
/// carry several webhooks with different rules and failure policies, so the webhook is the unit — the
/// configuration's name comes along to say which policy engine it belongs to.
/// </summary>
public sealed record AdmissionWebhook
{
    public required string Name { get; init; }

    /// <summary>The Validating- or MutatingWebhookConfiguration this webhook is declared in.</summary>
    public required string Configuration { get; init; }

    public AdmissionWebhookKind Kind { get; init; }

    public WebhookFailurePolicy FailurePolicy { get; init; } = WebhookFailurePolicy.Fail;

    public IReadOnlyList<WebhookRule> Rules { get; init; } = [];

    /// <summary>What the API server calls: "namespace/service" for an in-cluster service, else the URL.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>How long the API server waits before <see cref="FailurePolicy"/> decides.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    public TimeSpan Age { get; init; }
}
