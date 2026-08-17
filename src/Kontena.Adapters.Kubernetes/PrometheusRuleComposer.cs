using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Turns an <see cref="AuthoredRule"/> into the <c>PrometheusRule</c> document that gets applied and
/// written. One function, one output, and both destinations get the same string.
/// <para>
/// <b>Nothing is injected.</b> No <c>managed-by</c> label, no timestamp, no annotation recording that
/// Kontena was here. The bytes in a repository and the object in the cluster are the same bytes —
/// anything added for Kontena's own benefit would make the file a story about the cluster instead of
/// its source, and the preview panel promises the opposite.
/// </para>
/// <para>
/// Written out by hand rather than through a serializer, for the same reason
/// <see cref="ManifestNormalizer"/> is: a serializer picks quoting, key order and block style for
/// itself, and those choices move with its version. This document has one fixed shape, and a
/// byte-for-byte promise cannot be delegated to something that may reformat it next release.
/// </para>
/// <para>
/// It lives here rather than in Core because it is the CRD's group and version that make it a
/// Kubernetes fact; <see cref="AuthoredRule"/> itself knows nothing about either.
/// </para>
/// </summary>
public static class PrometheusRuleComposer
{
    /// <summary>The Operator's CRD this composes for.</summary>
    public const string ApiVersion = "monitoring.coreos.com/v1";

    /// <inheritdoc cref="ApiVersion"/>
    public const string Kind = "PrometheusRule";

    /// <summary>
    /// The manifest for one authored rule.
    /// <para>
    /// Map order is the caller's, not sorted: the editor writes <c>severity</c> first and then the
    /// rows as they appear on screen, and a manifest whose labels come back alphabetised does not
    /// look like the form that produced it. Deterministic either way, which is all the promise needs.
    /// </para>
    /// </summary>
    public static string Compose(AuthoredRule rule)
    {
        var yaml = new StringBuilder();

        yaml.Append("apiVersion: ").Append(ApiVersion).Append('\n');
        yaml.Append("kind: ").Append(Kind).Append('\n');
        yaml.Append("metadata:\n");
        yaml.Append("  name: ").Append(ManifestNormalizer.Quote(rule.ObjectName)).Append('\n');
        yaml.Append("  namespace: ").Append(ManifestNormalizer.Quote(rule.Namespace)).Append('\n');

        // metadata.labels, not the alert's — this is what ruleSelector tests, and the one thing that
        // decides whether the object is ever looked at.
        AppendMap(yaml, "labels", rule.ObjectLabels, indent: 2);

        yaml.Append("spec:\n");
        yaml.Append("  groups:\n");
        yaml.Append("    - name: ").Append(ManifestNormalizer.Quote(GroupNameOf(rule))).Append('\n');
        yaml.Append("      rules:\n");
        yaml.Append("        - alert: ").Append(ManifestNormalizer.Quote(rule.Name)).Append('\n');

        AppendExpr(yaml, rule.Expr, indent: 10);

        if (rule.For is { } wait)
            yaml.Append("          for: ").Append(PromDuration.Format(wait)).Append('\n');

        AppendMap(yaml, "labels", rule.Labels, indent: 10);
        AppendMap(yaml, "annotations", rule.Annotations, indent: 10);

        return yaml.ToString();
    }

    /// <summary>The group the rule lands in — <see cref="AuthoredRule.ObjectName"/> when unnamed.</summary>
    public static string GroupNameOf(AuthoredRule rule) =>
        rule.GroupName.Length > 0 ? rule.GroupName : rule.ObjectName;

    private static void AppendMap(
        StringBuilder yaml, string key, IReadOnlyDictionary<string, string> entries, int indent)
    {
        if (entries.Count == 0)
            return;

        var pad = new string(' ', indent);
        yaml.Append(pad).Append(key).Append(":\n");

        foreach (var (name, value) in entries)
        {
            yaml.Append(pad).Append("  ")
                .Append(ManifestNormalizer.Quote(name)).Append(": ")
                .Append(ManifestNormalizer.Quote(value)).Append('\n');
        }
    }

    /// <summary>
    /// The expression, as a literal block wherever a bare scalar will not carry it.
    /// <para>
    /// PromQL is full of the characters that make a scalar ambiguous — braces, brackets, quotes — and
    /// a double-quoted expression full of <c>\"</c> is unreadable in exactly the panel that exists to
    /// be read. A literal block needs no escaping at all, so it is what anything but the simplest
    /// expression gets.
    /// </para>
    /// </summary>
    private static void AppendExpr(StringBuilder yaml, string expr, int indent)
    {
        var pad = new string(' ', indent);

        // Outer whitespace goes first, and it is not cosmetic: a literal block takes its indentation
        // from the first content line, so an expression that starts indented would set a block indent
        // the following lines then fall out of. Trailing whitespace per line goes for the same
        // reason — a line of spaces inside the block is a parse hazard nobody can see in the editor.
        var lines = expr
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim()
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        if (lines is [""])
        {
            yaml.Append(pad).Append("expr: \"\"\n");
            return;
        }

        if (lines is [var only] && ManifestNormalizer.Quote(only) == only)
        {
            yaml.Append(pad).Append("expr: ").Append(only).Append('\n');
            return;
        }

        // "-" strips the final line break, so the value is the expression and not the expression plus
        // a newline — which is what a round-trip through the cluster would otherwise disagree about.
        yaml.Append(pad).Append("expr: |-\n");
        foreach (var line in lines)
            yaml.Append(line.Length == 0 ? string.Empty : pad + "  ").Append(line).Append('\n');
    }
}

/// <summary>
/// Prometheus' own duration syntax (<c>10m</c>, <c>1h30m</c>), which is not .NET's and not ISO 8601.
/// <para>
/// Both halves are needed and by different callers: the editor parses what someone types into the
/// <c>for</c> field, and the composer writes it back out. Round-tripping through one grammar is what
/// stops <c>90s</c> from being applied as <c>PT1M30S</c>, which the Operator rejects.
/// </para>
/// </summary>
public static class PromDuration
{
    // Units must appear largest-first and each at most once — Prometheus' own rule, and the reason
    // "1m30s" parses while "30s1m" does not. Anchored, so a trailing typo fails rather than being
    // silently ignored.
    private static readonly Regex Grammar = new(
        @"^(?:(\d+)y)?(?:(\d+)w)?(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?(?:(\d+)ms)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly double[] UnitMs = [365 * 86_400_000d, 7 * 86_400_000d, 86_400_000, 3_600_000, 60_000, 1000, 1];

    /// <summary>Parse a Prometheus duration. False for anything it would not accept either.</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Grammar.Match(text.Trim());

        // The grammar is entirely optional groups, so it also matches the empty string — and every
        // group being absent is the one case that has to be refused rather than read as zero.
        if (!match.Success || !match.Groups.Cast<Group>().Skip(1).Any(g => g.Success))
            return false;

        var total = 0d;
        for (var i = 0; i < UnitMs.Length; i++)
        {
            if (match.Groups[i + 1] is { Success: true } part)
                total += double.Parse(part.Value, CultureInfo.InvariantCulture) * UnitMs[i];
        }

        value = TimeSpan.FromMilliseconds(total);
        return true;
    }

    /// <summary>Write a duration the way Prometheus writes it. Years and weeks are never emitted —
    /// they are accepted on the way in and read back as days, which is unambiguous.</summary>
    public static string Format(TimeSpan value)
    {
        var ms = (long)Math.Round(value.TotalMilliseconds);
        if (ms <= 0)
            return "0s";

        var text = new StringBuilder();
        foreach (var (suffix, size) in new (string Suffix, long Size)[]
                 { ("d", 86_400_000), ("h", 3_600_000), ("m", 60_000), ("s", 1000), ("ms", 1) })
        {
            var whole = ms / size;
            if (whole == 0)
                continue;

            text.Append(whole.ToString(CultureInfo.InvariantCulture)).Append(suffix);
            ms -= whole * size;
        }

        return text.ToString();
    }
}
