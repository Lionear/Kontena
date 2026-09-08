using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Trimming a CRD's schema description down to a picker line (KON-455). These descriptions are written
/// as reference documentation — paragraphs, with version numbers and "e.g." in them — and the row that
/// shows them has one line.
/// </summary>
public sealed class CrdDescriptionTests
{
    [Fact]
    public void The_first_sentence_is_enough()
    {
        Assert.Equal(
            "A TLS certificate.",
            ApiResourceResolver.FirstSentence("A TLS certificate. The rest is reference material."));
    }

    /// <summary>A full stop inside a version or an abbreviation is not the end of the sentence.</summary>
    [Fact]
    public void A_full_stop_that_is_not_a_sentence_end_does_not_cut_it_short()
    {
        Assert.Equal(
            "Runs Dragonfly v1.21.2 in this cluster.",
            ApiResourceResolver.FirstSentence("Runs Dragonfly v1.21.2 in this cluster. More below."));
    }

    [Fact]
    public void A_description_with_no_sentence_end_is_cut_on_a_word_and_says_so()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 80));
        var trimmed = ApiResourceResolver.FirstSentence(text);

        Assert.EndsWith("…", trimmed, StringComparison.Ordinal);
        Assert.True(trimmed.Length <= 161, $"was {trimmed.Length}");
        Assert.DoesNotContain("wor…", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_description_is_left_alone()
    {
        Assert.Equal("Just this", ApiResourceResolver.FirstSentence("Just this"));
    }

    /// <summary>Line breaks in the schema must not reach the row as gaps.</summary>
    [Fact]
    public void Whitespace_is_collapsed()
    {
        Assert.Equal("One two three", ApiResourceResolver.FirstSentence("One  two\n\n  three"));
    }
}
