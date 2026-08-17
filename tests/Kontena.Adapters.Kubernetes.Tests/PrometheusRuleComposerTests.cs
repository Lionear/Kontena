using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The composer (KON-210). The promise under test is narrow and load-bearing: what the preview panel
/// shows is what gets applied and what KON-211 writes, with nothing added in between.
/// </summary>
public class PrometheusRuleComposerTests
{
    private static AuthoredRule Rule() => new()
    {
        Name = "AppHighErrorRate",
        Expr = "sum(rate(http_requests_total{job=\"checkout\",status=~\"5..\"}[5m]))\n"
            + "  / sum(rate(http_requests_total{job=\"checkout\"}[5m])) > 0.05",
        For = TimeSpan.FromMinutes(10),
        Labels = new Dictionary<string, string> { ["severity"] = "critical", ["team"] = "payments" },
        Annotations = new Dictionary<string, string> { ["summary"] = "Checkout 5xx rate above 5%" },
        ObjectName = "checkout-slo",
        Namespace = "monitoring",
        ObjectLabels = new Dictionary<string, string> { ["release"] = "kube-prometheus-stack" },
    };

    [Fact]
    public void A_rule_composes_to_the_document_the_preview_panel_shows()
    {
        Assert.Equal(
            """
            apiVersion: monitoring.coreos.com/v1
            kind: PrometheusRule
            metadata:
              name: checkout-slo
              namespace: monitoring
              labels:
                release: kube-prometheus-stack
            spec:
              groups:
                - name: checkout-slo
                  rules:
                    - alert: AppHighErrorRate
                      expr: |-
                        sum(rate(http_requests_total{job="checkout",status=~"5.."}[5m]))
                          / sum(rate(http_requests_total{job="checkout"}[5m])) > 0.05
                      for: 10m
                      labels:
                        severity: critical
                        team: payments
                      annotations:
                        summary: Checkout 5xx rate above 5%

            """.ReplaceLineEndings("\n"),
            PrometheusRuleComposer.Compose(Rule()));
    }

    /// <summary>
    /// The one thing that would break KON-211's byte-identity promise, so it is asserted about the
    /// text rather than left to a reading of the code.
    /// </summary>
    [Fact]
    public void Nothing_is_injected_for_Kontenas_own_benefit()
    {
        var yaml = PrometheusRuleComposer.Compose(Rule());

        Assert.DoesNotContain("managed-by", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kontena", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creationTimestamp", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("last-applied", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The selector label goes on <c>metadata</c>, never into the alert's labels. Putting it in the
    /// wrong one is silent: the object applies, Prometheus ignores it, and the alert carries a label
    /// nobody asked for.
    /// </summary>
    [Fact]
    public void The_selector_label_lands_on_the_object_and_not_on_the_alert()
    {
        var yaml = PrometheusRuleComposer.Compose(Rule());
        var alertLabels = yaml[yaml.IndexOf("      labels:", StringComparison.Ordinal)..];

        Assert.Contains("  labels:\n    release: kube-prometheus-stack\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("release", alertLabels, StringComparison.Ordinal);
    }

    [Fact]
    public void An_omitted_for_and_empty_maps_leave_their_keys_out_entirely()
    {
        var yaml = PrometheusRuleComposer.Compose(new AuthoredRule
        {
            Name = "Up",
            Expr = "up == 0",
            ObjectName = "up",
            Namespace = "monitoring",
        });

        Assert.Equal(
            """
            apiVersion: monitoring.coreos.com/v1
            kind: PrometheusRule
            metadata:
              name: up
              namespace: monitoring
            spec:
              groups:
                - name: up
                  rules:
                    - alert: Up
                      expr: up == 0

            """.ReplaceLineEndings("\n"),
            yaml);
    }

    /// <summary>
    /// A one-liner full of braces would otherwise come out as a double-quoted scalar with escaped
    /// quotes — valid, and unreadable in the panel that exists to be read.
    /// </summary>
    [Fact]
    public void An_expression_that_would_need_quoting_becomes_a_literal_block_instead()
    {
        var yaml = PrometheusRuleComposer.Compose(new AuthoredRule
        {
            Name = "Up",
            Expr = "up{job=\"checkout\"} == 0",
            ObjectName = "up",
            Namespace = "monitoring",
        });

        Assert.Contains("expr: |-\n            up{job=\"checkout\"} == 0\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A literal block takes its indentation from the first content line, so an indented expression
    /// would set a block indent the rest of it then falls out of — an unparseable manifest from a
    /// form that looked fine.
    /// </summary>
    [Fact]
    public void Outer_whitespace_around_an_expression_does_not_reach_the_manifest()
    {
        var yaml = PrometheusRuleComposer.Compose(new AuthoredRule
        {
            Name = "Up",
            Expr = "\n    up == 0\n      or absent(up)   \n\n",
            ObjectName = "up",
            Namespace = "monitoring",
        });

        // The first content line sits flush at the block indent; the second keeps the relative
        // indent it was typed with, which is the only part of the whitespace that means anything.
        Assert.EndsWith(
            "expr: |-\n            up == 0\n                  or absent(up)\n",
            yaml, StringComparison.Ordinal);
    }

    /// <summary>Label values are strings to Kubernetes, and "true" is not a bool in a manifest.</summary>
    [Fact]
    public void Label_values_that_would_read_as_something_else_are_quoted()
    {
        var yaml = PrometheusRuleComposer.Compose(new AuthoredRule
        {
            Name = "Up",
            Expr = "up == 0",
            Labels = new Dictionary<string, string> { ["paging"] = "true", ["ratio"] = "0.05" },
            ObjectName = "up",
            Namespace = "monitoring",
        });

        Assert.Contains("paging: \"true\"", yaml, StringComparison.Ordinal);
        Assert.Contains("ratio: \"0.05\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_falls_back_to_the_object_name_and_is_used_when_given()
    {
        var rule = Rule() with { GroupName = "checkout" };

        Assert.Contains("- name: checkout\n", PrometheusRuleComposer.Compose(rule), StringComparison.Ordinal);
        Assert.Contains("- name: checkout-slo\n", PrometheusRuleComposer.Compose(Rule()), StringComparison.Ordinal);
    }
}

/// <summary>
/// Prometheus' duration grammar, which is neither .NET's nor ISO 8601 — and the Operator rejects
/// anything else, so a round-trip that "nearly" works is a rule that never gets created.
/// </summary>
public class PromDurationTests
{
    [Theory]
    [InlineData("10m", 600)]
    [InlineData("30s", 30)]
    [InlineData("1h30m", 5400)]
    [InlineData("2d", 172800)]
    [InlineData("500ms", 0.5)]
    [InlineData(" 5m ", 300)]
    public void Prometheus_durations_parse_to_the_time_they_mean(string text, double seconds)
    {
        Assert.True(PromDuration.TryParse(text, out var value));
        Assert.Equal(seconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("10")]
    [InlineData("10 m")]
    [InlineData("ten minutes")]
    [InlineData("30s1m")]
    [InlineData("PT10M")]
    public void Anything_Prometheus_would_refuse_is_refused_here_too(string? text)
    {
        Assert.False(PromDuration.TryParse(text, out _));
    }

    [Theory]
    [InlineData(600, "10m")]
    [InlineData(5400, "1h30m")]
    [InlineData(90, "1m30s")]
    [InlineData(0, "0s")]
    public void A_duration_writes_back_out_the_way_Prometheus_writes_it(int seconds, string expected)
    {
        Assert.Equal(expected, PromDuration.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("10m")]
    [InlineData("1h30m")]
    [InlineData("2d4h")]
    [InlineData("1m30s")]
    public void What_someone_types_survives_the_round_trip_into_the_manifest(string text)
    {
        Assert.True(PromDuration.TryParse(text, out var value));
        Assert.Equal(text, PromDuration.Format(value));
    }
}
