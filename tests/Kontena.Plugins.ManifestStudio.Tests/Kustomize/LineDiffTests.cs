using Kontena.Plugins.ManifestStudio.Kustomize;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class LineDiffTests
{
    [Fact]
    public void Identical_sequences_are_all_same()
    {
        string[] lines = ["a", "b", "c"];

        var diff = LineDiff.Compare(lines, lines);

        Assert.All(diff, e => Assert.Equal(LineDiffKind.Same, e.Kind));
        Assert.Equal(3, diff.Count);
    }

    [Fact]
    public void A_single_changed_line_does_not_disturb_its_neighbours()
    {
        string[] before = ["a", "b", "c"];
        string[] after = ["a", "X", "c"];

        var diff = LineDiff.Compare(before, after);

        Assert.Equal(LineDiffKind.Same, diff[0].Kind);
        Assert.Equal(LineDiffKind.Changed, diff[1].Kind);
        Assert.Equal("b", diff[1].BaseText);
        Assert.Equal("X", diff[1].OverlayText);
        Assert.Equal(LineDiffKind.Same, diff[2].Kind);
    }

    [Fact]
    public void An_inserted_line_does_not_misalign_the_rest_as_changed()
    {
        string[] before = ["a", "b", "c"];
        string[] after = ["a", "new", "b", "c"];

        var diff = LineDiff.Compare(before, after);

        Assert.Equal(1, diff.Count(e => e.Kind == LineDiffKind.Added));
        Assert.Equal(3, diff.Count(e => e.Kind == LineDiffKind.Same));
        Assert.DoesNotContain(diff, e => e.Kind == LineDiffKind.Changed);
    }

    [Fact]
    public void A_removed_line_is_reported_once_not_as_a_cascade_of_changes()
    {
        string[] before = ["a", "b", "c"];
        string[] after = ["a", "c"];

        var diff = LineDiff.Compare(before, after);

        var removed = Assert.Single(diff, e => e.Kind == LineDiffKind.Removed);
        Assert.Equal("b", removed.BaseText);
        Assert.Equal(2, diff.Count(e => e.Kind == LineDiffKind.Same));
    }

    [Fact]
    public void Multiple_independent_changes_are_each_reported()
    {
        string[] before = ["a", "b", "c", "d"];
        string[] after = ["A", "b", "C", "d"];

        var diff = LineDiff.Compare(before, after);

        Assert.Equal(2, diff.Count(e => e.Kind == LineDiffKind.Changed));
        Assert.Equal(2, diff.Count(e => e.Kind == LineDiffKind.Same));
    }
}
