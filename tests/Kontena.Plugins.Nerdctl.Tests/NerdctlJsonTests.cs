using Kontena.Plugins.Nerdctl;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// The four places nerdctl's output differs from what a reader would assume. Every string in here was
/// observed from nerdctl 2.3.5, not taken from its documentation — see Notes/nerdctl-cli-formats.md.
/// </summary>
public sealed class NerdctlJsonTests
{
    [Fact]
    public void Output_is_one_object_per_line_not_an_array()
    {
        const string ndjson = """
            {"Name":"default"}
            {"Name":"k8s.io"}
            """;

        Assert.Equal(2, NerdctlJson.Lines(ndjson).Count());
    }

    [Fact]
    public void Empty_output_yields_nothing_rather_than_throwing()
    {
        // `volume ls --format json` prints nothing at all when there are no volumes — not [], not a
        // blank line. A parser that expects at least something fails on an ordinary machine.
        Assert.Empty(NerdctlJson.Lines(""));
        Assert.Empty(NerdctlJson.Lines("\n"));
    }

    [Theory]
    [InlineData("53.98MB", 53_980_000L)]
    [InlineData("20.97MB", 20_970_000L)]
    [InlineData("1.5GB", 1_500_000_000L)]
    [InlineData("742B", 742L)]
    [InlineData("0B", 0L)]
    public void Sizes_are_human_strings_with_a_unit(string text, long expected)
    {
        // Docker's API gives bytes; nerdctl gives what its table would print.
        Assert.Equal(expected, NerdctlJson.Size(text));
    }

    [Fact]
    public void An_unreadable_size_is_zero_rather_than_an_exception()
    {
        // A size we cannot read is a cosmetic loss. Throwing here would cost the whole image list.
        Assert.Equal(0L, NerdctlJson.Size(""));
        Assert.Equal(0L, NerdctlJson.Size("nonsense"));
    }

    [Fact]
    public void Ps_and_images_use_different_date_formats()
    {
        // ps: ISO8601. images: Go's default layout. One parser is not enough.
        Assert.Equal(
            new DateTimeOffset(2026, 8, 2, 8, 42, 0, TimeSpan.Zero),
            NerdctlJson.Time("2026-08-02T08:42:00.860762129Z"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 30, 22, 10, 58, TimeSpan.Zero),
            NerdctlJson.Time("2026-07-30 22:10:58 +0000 UTC"),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void An_unreadable_date_is_default_rather_than_an_exception()
    {
        Assert.Equal(default, NerdctlJson.Time("whenever"));
    }

    [Fact]
    public void Labels_are_one_comma_joined_string_not_an_object()
    {
        var labels = NerdctlJson.Labels(
            "io.cri-containerd.kind=container,io.kubernetes.pod.name=local-path-provisioner-855c7b7774-vw7t9");

        Assert.Equal(2, labels.Count);
        Assert.Equal("container", labels["io.cri-containerd.kind"]);
    }

    [Fact]
    public void A_label_value_may_contain_an_equals_sign()
    {
        Assert.Equal("a=b", NerdctlJson.Labels("k=a=b")["k"]);
    }

    [Fact]
    public void No_labels_is_an_empty_map()
    {
        Assert.Empty(NerdctlJson.Labels(""));
    }
}
