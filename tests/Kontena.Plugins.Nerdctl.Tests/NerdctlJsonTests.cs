using System.Globalization;
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

    [Theory]
    // The fixture only ever shows "+0000 UTC" because it was captured inside a container with no
    // timezone set — nerdctl formats this column with Go's `.Local()` (pkg/cmd/image/list.go), so on
    // any machine with a timezone the offset and zone name are whatever the host uses. Each case here
    // asserts the correct instant, not merely "not default" — that would pass even if the zone name
    // were silently mis-parsed as an offset of its own.
    [InlineData("2026-07-30 22:10:58 +0200 CEST", "2026-07-30T20:10:58Z")]
    [InlineData("2021-08-07 02:19:45 +0900 JST", "2021-08-06T17:19:45Z")]
    [InlineData("2026-07-30 18:10:58 -0400 EDT", "2026-07-30T22:10:58Z")]
    public void A_non_utc_go_timestamp_still_resolves_to_the_right_instant(string text, string expectedUtc)
    {
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture), NerdctlJson.Time(text));
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

    // ── stats: binary units, paired fields, percentages ─────────────────────────────────────────

    [Fact]
    public void BinarySize_reads_stats_units_as_powers_of_1024()
    {
        Assert.Equal(1024, NerdctlJson.BinarySize("1KiB"));
        Assert.Equal(2 * 1024 * 1024, NerdctlJson.BinarySize("2MiB"));
        Assert.Equal(1024L * 1024 * 1024, NerdctlJson.BinarySize("1GiB"));
        Assert.Equal(0, NerdctlJson.BinarySize("0B"));
    }

    [Fact]
    public void BinarySize_and_Size_disagree_because_stats_and_images_disagree()
    {
        // The whole reason two parsers exist: MiB is 1024-based, MB is 1000-based, and reading one with
        // the other is off by ~5% with nothing in the output to reveal it.
        Assert.Equal(1_048_576, NerdctlJson.BinarySize("1MiB"));
        Assert.Equal(1_000_000, NerdctlJson.Size("1MB"));
        Assert.NotEqual(NerdctlJson.Size("1MB"), NerdctlJson.BinarySize("1MiB"));
    }

    [Fact]
    public void BinarySize_does_not_accept_the_decimal_units_images_prints()
    {
        // "MB" is not a unit `stats` ever prints; reading it as 1024-based would quietly invent a value.
        Assert.Equal(0, NerdctlJson.BinarySize("53.98MB"));
    }

    [Fact]
    public void A_real_stats_memory_figure_lands_between_13_and_14_megabytes()
    {
        var bytes = NerdctlJson.BinarySize("13.11MiB");

        Assert.InRange(bytes, 13_000_000, 14_000_000);
    }

    [Fact]
    public void Pair_splits_the_two_values_stats_packs_into_one_field()
    {
        Assert.Equal(("13.11MiB", "62.7GiB"), NerdctlJson.Pair("13.11MiB / 62.7GiB"));
        Assert.Equal(("0B", "0B"), NerdctlJson.Pair("0B / 0B"));
    }

    [Fact]
    public void Pair_without_a_separator_keeps_the_whole_text_as_the_first_half()
    {
        Assert.Equal(("whatever", ""), NerdctlJson.Pair("whatever"));
    }

    [Fact]
    public void Percent_strips_the_sign_nerdctl_prints()
    {
        Assert.Equal(0, NerdctlJson.Percent("0.00%"));
        Assert.Equal(12.5, NerdctlJson.Percent("12.5%"));
        Assert.Equal(0, NerdctlJson.Percent(""));
    }

    // ── compose: logrus lines ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Logrus_unwraps_the_message_compose_narrates_with()
    {
        var (level, message) = NerdctlJson.Logrus("level=info msg=\"Creating container cmp-web-1\"");

        Assert.Equal("info", level);
        Assert.Equal("Creating container cmp-web-1", message);
    }

    [Fact]
    public void Logrus_leaves_a_line_that_is_not_logrus_shaped_alone()
    {
        var (level, message) = NerdctlJson.Logrus("just a line");

        Assert.Null(level);
        Assert.Equal("just a line", message);
    }

    // ── events: the id nested in an escaped JSON string ─────────────────────────────────────────

    [Fact]
    public void NestedId_reads_the_id_out_of_a_containers_event_payload()
    {
        Assert.Equal("62091b25", NerdctlJson.NestedId("""{"id":"62091b25","image":"nginx:latest"}"""));
    }

    [Fact]
    public void NestedId_falls_back_to_name_then_key_for_the_topics_that_use_those()
    {
        Assert.Equal("docker.io/library/nginx:latest", NerdctlJson.NestedId("""{"name":"docker.io/library/nginx:latest"}"""));
        Assert.Equal("62091b25", NerdctlJson.NestedId("""{"key":"62091b25","snapshotter":"overlayfs"}"""));
    }

    [Fact]
    public void NestedId_of_something_unreadable_is_empty_rather_than_a_throw()
    {
        // An event stream that dies on one unfamiliar payload stops reporting every later event too.
        Assert.Equal(string.Empty, NerdctlJson.NestedId("not json"));
        Assert.Equal(string.Empty, NerdctlJson.NestedId(""));
        Assert.Equal(string.Empty, NerdctlJson.NestedId("""{"runtime":{"name":"io.containerd.runc.v2"}}"""));
    }
}
