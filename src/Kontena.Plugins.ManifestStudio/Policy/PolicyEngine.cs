using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Policy;

/// <summary>
/// KON-297: deterministic house rules over the rendered bundle, in the same spirit as
/// <c>ManifestDiagnostics</c>'s schema authority — no match, no finding, never a guess. Reported with
/// <see cref="DiagnosticAuthority.Policy"/> so they never read as a schema violation; the schema is not
/// wrong just because a container has no <c>resources.requests</c>.
/// <para>
/// A pure function of the bundle and the config, same shape as <c>ManifestDiagnostics.Validate</c> and
/// for the same reason: the caller loads <see cref="PolicyConfigFile"/> once (I/O), this does none.
/// </para>
/// </summary>
public static class PolicyEngine
{
    public static IReadOnlyList<Diagnostic> Validate(string bundle, PolicyConfig config)
    {
        var enabledRules = config.Rules.Where(r => r.Enabled).ToArray();
        if (enabledRules.Length == 0)
            return [];

        var diagnostics = new List<Diagnostic>();

        foreach (var (text, lineOffset) in ManifestDiagnostics.SplitDocuments(bundle))
        {
            var document = YamlOutline.Parse(text);
            foreach (var rule in enabledRules)
                diagnostics.AddRange(Evaluate(rule, document, lineOffset));
        }

        return diagnostics;
    }

    private static IEnumerable<Diagnostic> Evaluate(PolicyRuleConfig rule, YamlOutline document, int lineOffset) =>
        rule.Id switch
        {
            PolicyRuleId.ContainersDeclareRequests => ContainersDeclareRequests(document, lineOffset),
            PolicyRuleId.NoLatestImageTag => NoLatestImageTag(document, lineOffset),
            PolicyRuleId.ReadinessProbeRequired => ReadinessProbeRequired(document, lineOffset),
            PolicyRuleId.RequiredLabels => RequiredLabelsCheck(document, lineOffset, rule.RequiredLabels ?? []),
            _ => [],
        };

    private static IEnumerable<Diagnostic> ContainersDeclareRequests(YamlOutline document, int lineOffset)
    {
        foreach (var container in Containers(document))
        {
            var requests = container.Children.FirstOrDefault(c => c.Key == "resources")
                ?.Children.FirstOrDefault(c => c.Key == "requests");

            if (requests is null)
                yield return Finding(lineOffset + container.Line, $"Container '{ContainerName(container)}' declares no resources.requests.");
        }
    }

    private static IEnumerable<Diagnostic> NoLatestImageTag(YamlOutline document, int lineOffset)
    {
        foreach (var container in Containers(document))
        {
            var image = container.Children.FirstOrDefault(c => c.Key == "image");
            if (image?.InlineValue is not { } reference)
                continue;

            if (ResolvesToLatest(reference))
                yield return Finding(lineOffset + image.Line, $"Image '{reference}' resolves to :latest.");
        }
    }

    private static IEnumerable<Diagnostic> ReadinessProbeRequired(YamlOutline document, int lineOffset)
    {
        foreach (var container in Containers(document))
        {
            if (container.Children.All(c => c.Key != "readinessProbe"))
                yield return Finding(lineOffset + container.Line, $"Container '{ContainerName(container)}' has no readinessProbe.");
        }
    }

    private static IEnumerable<Diagnostic> RequiredLabelsCheck(YamlOutline document, int lineOffset, IReadOnlyList<string> required)
    {
        var metadata = document.Children.FirstOrDefault(c => c.Key == "metadata");
        var labels = metadata?.Children.FirstOrDefault(c => c.Key == "labels");
        var present = labels?.Children.Where(c => c.Key is not null).Select(c => c.Key!).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var anchor = lineOffset + (metadata?.Line ?? Math.Max(document.Line, 0));

        foreach (var label in required)
            if (!present.Contains(label))
                yield return Finding(anchor, $"Missing required label '{label}'.");
    }

    /// <summary>Every "containers:" list in the document, regardless of which kind wraps it — a
    /// Deployment, a bare Pod and a CronJob each nest the pod spec at a different depth, and this rule
    /// does not need to know which.</summary>
    private static IEnumerable<YamlOutline> Containers(YamlOutline document) =>
        FindLists(document, "containers").SelectMany(list => list.Children.Where(c => c.IsArrayItem));

    private static IEnumerable<YamlOutline> FindLists(YamlOutline node, string key)
    {
        if (node.Key == key)
            yield return node;

        foreach (var child in node.Children)
            foreach (var found in FindLists(child, key))
                yield return found;
    }

    private static string ContainerName(YamlOutline container) =>
        container.Children.FirstOrDefault(c => c.Key == "name")?.InlineValue ?? "(unnamed)";

    /// <summary>A tag comes after the *last* '/' — a registry port ("host:5000/image") has a colon
    /// before it that is not a tag. A digest reference ("image@sha256:...") is more precise than any
    /// tag and is never "latest", however plain its own image name reads.</summary>
    private static bool ResolvesToLatest(string reference)
    {
        if (reference.Contains('@'))
            return false;

        var lastSlash = reference.LastIndexOf('/');
        var tagColon = reference.IndexOf(':', lastSlash + 1);

        return tagColon < 0 || reference[(tagColon + 1)..] == "latest";
    }

    private static Diagnostic Finding(int line, string message) =>
        new(DiagnosticAuthority.Policy, DiagnosticSeverity.Warning, line, message);
}
