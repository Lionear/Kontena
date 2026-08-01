using Kontena.Plugins.ManifestStudio.Policy;
using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Tests.Policy;

public sealed class PolicyEngineTests
{
    private static PolicyConfig With(params PolicyRuleConfig[] rules) => new(rules);

    [Fact]
    public void The_default_config_has_every_rule_off_so_nothing_is_ever_reported()
    {
        const string bundle = "kind: Deployment\nspec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: nginx:latest\n";

        Assert.Empty(PolicyEngine.Validate(bundle, PolicyConfig.Default));
    }

    [Fact]
    public void A_container_without_resource_requests_is_reported_when_enabled()
    {
        const string bundle = """
            kind: Deployment
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    image: nginx:1.27
            """;
        var config = With(new PolicyRuleConfig(PolicyRuleId.ContainersDeclareRequests, Enabled: true));

        var finding = Assert.Single(PolicyEngine.Validate(bundle, config));

        Assert.Equal(DiagnosticAuthority.Policy, finding.Authority);
        Assert.Contains("app", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_with_resource_requests_is_not_reported()
    {
        const string bundle = """
            kind: Deployment
            spec:
              template:
                spec:
                  containers:
                  - name: app
                    resources:
                      requests:
                        cpu: 100m
            """;
        var config = With(new PolicyRuleConfig(PolicyRuleId.ContainersDeclareRequests, Enabled: true));

        Assert.Empty(PolicyEngine.Validate(bundle, config));
    }

    [Theory]
    [InlineData("nginx", true)] // no tag at all -> implicit latest
    [InlineData("nginx:latest", true)]
    [InlineData("nginx:1.27", false)]
    [InlineData("registry.example.com:5000/team/app", true)] // port, not a tag -> implicit latest
    [InlineData("registry.example.com:5000/team/app:v2", false)]
    [InlineData("app@sha256:abcdef0123456789", false)] // digest-pinned, never "latest"
    public void Image_tags_are_classified_correctly(string image, bool expectedFinding)
    {
        var bundle = $"kind: Pod\nspec:\n  containers:\n  - name: app\n    image: {image}\n";
        var config = With(new PolicyRuleConfig(PolicyRuleId.NoLatestImageTag, Enabled: true));

        var findings = PolicyEngine.Validate(bundle, config);

        Assert.Equal(expectedFinding, findings.Count == 1);
    }

    [Fact]
    public void A_container_without_a_readiness_probe_is_reported_when_enabled()
    {
        const string bundle = "kind: Pod\nspec:\n  containers:\n  - name: app\n    image: nginx\n";
        var config = With(new PolicyRuleConfig(PolicyRuleId.ReadinessProbeRequired, Enabled: true));

        Assert.Single(PolicyEngine.Validate(bundle, config));
    }

    [Fact]
    public void Missing_required_labels_are_reported_one_per_label()
    {
        const string bundle = "kind: Deployment\nmetadata:\n  name: web\n  labels:\n    app.kubernetes.io/name: web\n";
        var config = With(new PolicyRuleConfig(
            PolicyRuleId.RequiredLabels, Enabled: true,
            RequiredLabels: ["app.kubernetes.io/name", "app.kubernetes.io/part-of"]));

        var finding = Assert.Single(PolicyEngine.Validate(bundle, config));
        Assert.Contains("part-of", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_required_labels_present_reports_nothing()
    {
        const string bundle = "kind: Deployment\nmetadata:\n  name: web\n  labels:\n    app.kubernetes.io/name: web\n";
        var config = With(new PolicyRuleConfig(
            PolicyRuleId.RequiredLabels, Enabled: true, RequiredLabels: ["app.kubernetes.io/name"]));

        Assert.Empty(PolicyEngine.Validate(bundle, config));
    }

    [Fact]
    public void Enabled_with_no_configured_labels_reports_nothing()
    {
        const string bundle = "kind: Deployment\nmetadata:\n  name: web\n";
        var config = With(new PolicyRuleConfig(PolicyRuleId.RequiredLabels, Enabled: true));

        Assert.Empty(PolicyEngine.Validate(bundle, config));
    }

    [Theory]
    [InlineData("kind: Pod\nspec:\n  containers:\n  - name: app\n")] // Pod: spec.containers
    [InlineData("kind: Deployment\nspec:\n  template:\n    spec:\n      containers:\n      - name: app\n")] // Deployment
    [InlineData("kind: CronJob\nspec:\n  jobTemplate:\n    spec:\n      template:\n        spec:\n          containers:\n          - name: app\n")] // CronJob, deepest nesting
    public void Containers_are_found_regardless_of_which_kind_nests_them(string bundle)
    {
        var config = With(new PolicyRuleConfig(PolicyRuleId.ReadinessProbeRequired, Enabled: true));

        var finding = Assert.Single(PolicyEngine.Validate(bundle, config));
        Assert.Contains("app", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_document_in_a_bundle_is_checked_independently()
    {
        const string bundle = """
            kind: Pod
            metadata:
              name: a
            spec:
              containers:
              - name: app
            ---
            kind: Pod
            metadata:
              name: b
            spec:
              containers:
              - name: app
                readinessProbe:
                  httpGet:
                    path: /health
            """;
        var config = With(new PolicyRuleConfig(PolicyRuleId.ReadinessProbeRequired, Enabled: true));

        Assert.Single(PolicyEngine.Validate(bundle, config));
    }
}
