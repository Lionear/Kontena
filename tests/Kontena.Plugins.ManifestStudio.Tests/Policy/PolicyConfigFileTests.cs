using Kontena.Plugins.ManifestStudio.Policy;

namespace Kontena.Plugins.ManifestStudio.Tests.Policy;

public sealed class PolicyConfigFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-policy-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void No_file_in_the_workspace_falls_back_to_every_rule_off()
    {
        var config = PolicyConfigFile.Load(_root);

        Assert.Equal(PolicyConfig.Default, config);
    }

    [Fact]
    public void An_enabled_rule_is_read()
    {
        var config = PolicyConfigFile.Parse("""
            rules:
              - id: containers-declare-requests
                enabled: true
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(PolicyRuleId.ContainersDeclareRequests, rule.Id);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void RequiredLabels_reads_the_bare_scalar_labels_list()
    {
        var config = PolicyConfigFile.Parse("""
            rules:
              - id: required-labels
                enabled: true
                labels:
                  - app.kubernetes.io/name
                  - app.kubernetes.io/part-of
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(["app.kubernetes.io/name", "app.kubernetes.io/part-of"], rule.RequiredLabels);
    }

    [Fact]
    public void An_unknown_rule_id_is_skipped_not_an_error()
    {
        var config = PolicyConfigFile.Parse("""
            rules:
              - id: some-future-rule-this-version-does-not-know
                enabled: true
              - id: no-latest-tag
                enabled: true
            """);

        var rule = Assert.Single(config.Rules);
        Assert.Equal(PolicyRuleId.NoLatestImageTag, rule.Id);
    }

    [Fact]
    public void A_file_actually_present_in_the_workspace_is_loaded()
    {
        File.WriteAllText(
            Path.Combine(_root, PolicyConfigFile.FileName),
            "rules:\n  - id: no-latest-tag\n    enabled: true\n");

        var config = PolicyConfigFile.Load(_root);

        Assert.True(Assert.Single(config.Rules).Enabled);
    }
}
