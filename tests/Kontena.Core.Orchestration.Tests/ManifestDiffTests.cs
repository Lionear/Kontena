using Kontena.Sdk.Orchestration;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class ManifestDiffTests
{
    [Fact]
    public void Identical_input_produces_no_diff()
    {
        Assert.Empty(ManifestDiff.Compute("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void Empty_input_produces_no_diff()
    {
        Assert.Empty(ManifestDiff.Compute(string.Empty, string.Empty));
    }

    [Fact]
    public void A_changed_line_shows_as_a_removal_and_an_addition_with_context()
    {
        var diff = ManifestDiff.Compute("spec:\n  replicas: 1\n  paused: false", "spec:\n  replicas: 2\n  paused: false");

        // Every line carries its marker column: ' ' for context, '-'/'+' for the change.
        Assert.Equal(" spec:\n-  replicas: 1\n+  replicas: 2\n   paused: false", diff);
    }

    [Fact]
    public void A_new_resource_is_all_additions()
    {
        var diff = ManifestDiff.Compute(string.Empty, "kind: Service\nmetadata:");

        Assert.Equal("+kind: Service\n+metadata:", diff);
    }

    [Fact]
    public void Unchanged_runs_between_two_changes_collapse()
    {
        string[] middle = [.. Enumerable.Range(0, 20).Select(i => $"line-{i}")];
        var live = string.Join('\n', ["first", .. middle, "last"]);
        var desired = string.Join('\n', ["first-changed", .. middle, "last-changed"]);

        var diff = ManifestDiff.Compute(live, desired);

        // Three lines of context survive at each end; the untouched middle collapses to one marker.
        Assert.Contains("…", diff, StringComparison.Ordinal);
        Assert.Contains(" line-2", diff, StringComparison.Ordinal);
        Assert.Contains(" line-17", diff, StringComparison.Ordinal);
        Assert.DoesNotContain(" line-10", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_at_the_end_is_not_marked_as_collapsed()
    {
        var diff = ManifestDiff.Compute(
            string.Join('\n', ["change-me", .. Enumerable.Range(0, 20).Select(i => $"line-{i}")]),
            string.Join('\n', ["changed", .. Enumerable.Range(0, 20).Select(i => $"line-{i}")]));

        Assert.DoesNotContain("…", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line-19", diff, StringComparison.Ordinal);
    }
}
