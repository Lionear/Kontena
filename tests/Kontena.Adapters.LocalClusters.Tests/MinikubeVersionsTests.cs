using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

/// <summary>
/// Narrowing <c>minikube config defaults kubernetes-version</c> down to a list worth putting in a
/// dropdown (KON-144). The sample is minikube v1.38.1's own output, not a shape we imagined — the
/// last time that distinction was skipped it cost every running cluster its Stop button (KON-142).
/// </summary>
public class MinikubeVersionsTests
{
    /// <summary>Verbatim head of the real output, which runs on for some forty more lines.</summary>
    private const string Sample = """
        * v1.35.1
        * v1.35.0
        * v1.35.0-rc.1
        * v1.35.0-rc.0
        * v1.35.0-beta.0
        * v1.35.0-alpha.3
        * v1.34.4
        * v1.34.3
        * v1.34.2
        * v1.34.1
        * v1.34.0
        * v1.34.0-rc.2
        * v1.33.7
        * v1.33.4
        * v1.32.11
        * v1.31.14
        * v1.30.0
        """;

    [Fact]
    public void The_newest_patch_of_each_of_the_newest_minors_is_offered()
    {
        var options = MinikubeVersions.Parse(Sample);

        // Four minors, newest first, one entry each. Not v1.30: the list has to end somewhere, and a
        // dropdown of forty is a list nobody reads.
        Assert.Equal(["v1.35.1", "v1.34.4", "v1.33.7", "v1.32.11"], options.Offered);
    }

    [Fact]
    public void Pre_releases_are_left_out()
    {
        // An alpha in a create form is a trap: it looks like a version and behaves like a build.
        Assert.DoesNotContain(MinikubeVersions.Parse(Sample).Offered, v => v.Contains('-', StringComparison.Ordinal));
    }

    [Fact]
    public void The_newest_stable_is_reported_as_the_default()
    {
        // This is minikube's own pick when asked for no version, so the form can name it rather than
        // saying "default" and leaving someone to guess which one that is.
        Assert.Equal("v1.35.1", MinikubeVersions.Parse(Sample).Default);
    }

    [Fact]
    public void Minikube_does_not_offer_the_version_kind_can_boot()
    {
        // The reason the list is per provisioner at all: kind boots v1.36.1 today and this minikube has
        // never heard of it. One shared list is wrong for one of them by construction.
        Assert.DoesNotContain("v1.36.1", MinikubeVersions.Parse(Sample).Offered);
        Assert.Contains("v1.36.1", KindVersions.Options.Offered);
    }

    [Fact]
    public void A_line_that_is_not_a_version_is_skipped()
    {
        const string output = """
            Available Kubernetes versions:
            * v1.35.1
            * stable
            *
            """;

        Assert.Equal(["v1.35.1"], MinikubeVersions.Parse(output).Offered);
    }

    [Fact]
    public void Bullets_are_not_required()
    {
        // The bullet is formatting, not contract. A version per line reads the same.
        Assert.Equal(["v1.35.1"], MinikubeVersions.Parse("v1.35.1\n").Offered);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Error: unknown command \"defaults\"")]
    public void Anything_unreadable_offers_nothing(string output)
    {
        var options = MinikubeVersions.Parse(output);

        // Nothing rather than a guess: the form then offers the tool's own default, which always works.
        Assert.Empty(options.Offered);
        Assert.Null(options.Default);
    }

    [Fact]
    public void Kind_offers_a_maintained_list_with_no_named_default()
    {
        // kind cannot be asked which image its release ships with, so the form says "Default for this
        // release" rather than naming a version it would be guessing at.
        Assert.Null(KindVersions.Options.Default);
        Assert.NotEmpty(KindVersions.Options.Offered);
    }
}
