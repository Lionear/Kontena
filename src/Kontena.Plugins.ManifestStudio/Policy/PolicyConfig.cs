namespace Kontena.Plugins.ManifestStudio.Policy;

/// <summary>The house rules KON-297 ships built in. Deliberately a fixed, hand-written set — Plan-style
/// reasoning applies here too: a user-scriptable rule language is a second product, not a feature.</summary>
public enum PolicyRuleId
{
    ContainersDeclareRequests,
    NoLatestImageTag,
    ReadinessProbeRequired,
    RequiredLabels,
}

/// <summary>One rule's on/off state, plus whatever parameters it needs (only <see cref="RequiredLabels"/>
/// so far, for <see cref="PolicyRuleId.RequiredLabels"/>).</summary>
public sealed record PolicyRuleConfig(PolicyRuleId Id, bool Enabled, IReadOnlyList<string>? RequiredLabels = null);

/// <summary>
/// A workspace's opted-in house rules. Every rule defaults to off — these are, by definition, "how
/// *you* want manifests to look" (the ticket's own words), not something to impose on a workspace that
/// never asked for it.
/// </summary>
public sealed record PolicyConfig(IReadOnlyList<PolicyRuleConfig> Rules)
{
    public static readonly PolicyConfig Default = new(
    [
        new PolicyRuleConfig(PolicyRuleId.ContainersDeclareRequests, Enabled: false),
        new PolicyRuleConfig(PolicyRuleId.NoLatestImageTag, Enabled: false),
        new PolicyRuleConfig(PolicyRuleId.ReadinessProbeRequired, Enabled: false),
        new PolicyRuleConfig(PolicyRuleId.RequiredLabels, Enabled: false),
    ]);
}
