using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Policy;

/// <summary>
/// Reads a workspace's house rules from a file **in the workspace** (KON-297: "zodat de regels met de
/// repo meereizen in plaats van in iemands lokale instellingen te wonen") — a team's policy is a fact
/// about the repo, not about whoever happens to have it open.
/// <para>
/// Format, via the same lenient <see cref="YamlOutline"/> reader every other Manifest Studio file uses:
/// </para>
/// <code>
/// rules:
///   - id: containers-declare-requests
///     enabled: true
///   - id: required-labels
///     enabled: true
///     labels:
///       - app.kubernetes.io/name
///       - app.kubernetes.io/part-of
/// </code>
/// An unknown rule id is skipped, not an error — a newer workspace's policy file opened by an older
/// Manifest Studio should degrade to "know less", not refuse to load at all.
/// </summary>
public static class PolicyConfigFile
{
    public const string FileName = ".manifest-studio-policy.yaml";

    public static PolicyConfig Load(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : PolicyConfig.Default;
    }

    public static PolicyConfig Parse(string yaml)
    {
        var root = YamlOutline.Parse(yaml);
        var rulesNode = root.Children.FirstOrDefault(c => c.Key == "rules");
        if (rulesNode is null)
            return PolicyConfig.Default;

        var rules = new List<PolicyRuleConfig>();
        foreach (var item in rulesNode.Children.Where(c => c.IsArrayItem))
        {
            var id = item.Children.FirstOrDefault(c => c.Key == "id")?.InlineValue;
            if (id is null || !TryParseId(id, out var ruleId))
                continue;

            var enabled = item.Children.FirstOrDefault(c => c.Key == "enabled")?.InlineValue == "true";

            var labelsNode = item.Children.FirstOrDefault(c => c.Key == "labels");
            var labels = labelsNode is null
                ? null
                : (IReadOnlyList<string>)
                    [.. labelsNode.Children.Where(c => c.InlineValue is not null).Select(c => c.InlineValue!)];

            rules.Add(new PolicyRuleConfig(ruleId, enabled, labels));
        }

        return new PolicyConfig(rules);
    }

    private static bool TryParseId(string text, out PolicyRuleId id)
    {
        switch (text)
        {
            case "containers-declare-requests": id = PolicyRuleId.ContainersDeclareRequests; return true;
            case "no-latest-tag": id = PolicyRuleId.NoLatestImageTag; return true;
            case "readiness-probe-required": id = PolicyRuleId.ReadinessProbeRequired; return true;
            case "required-labels": id = PolicyRuleId.RequiredLabels; return true;
            default: id = default; return false;
        }
    }
}
