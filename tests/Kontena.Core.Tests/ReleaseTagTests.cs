using System.Text.RegularExpressions;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Which git tags start a release build (KON-124).
/// <para>
/// The rule lives in <c>build.yml</c>, in bash, where nothing exercises it — so this reads the pattern
/// out of the workflow and runs the cases past it. It is not testing a regex for its own sake: a tag
/// build is unconditionally the stable channel, becomes GitHub's latest release, and rolls
/// <c>[Unreleased]</c> into a version while deleting the changelog fragments. Letting one extra tag
/// shape through does all three, and the build reports success either way.
/// </para>
/// </summary>
public class ReleaseTagTests
{
    private static readonly Regex TagPattern = new(ExtractPattern(), RegexOptions.None);

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("v1.0.0")]
    [InlineData("v10.20.30")]
    public void A_plain_version_tag_releases(string tag) =>
        Assert.Matches(TagPattern, tag);

    [Theory]
    [InlineData("v0.2.0-rc.1")]
    [InlineData("v0.2.0-rc1")]
    [InlineData("v0.2.0-beta")]
    [InlineData("v0.2.0-alpha.2")]
    [InlineData("v0.2.0-preview")]
    [InlineData("v0.2.0-nightly.20260726.1")]
    public void A_prerelease_tag_does_not(string tag) =>
        // This is the one that bit: a tag is CHANNEL=stable with no further check, so v0.2.0-rc.1
        // shipped a release candidate to everyone on stable, took over the "latest release" badge, and
        // archived [Unreleased] as [0.2.0-rc.1] — leaving the real 0.2.0 with empty notes.
        Assert.DoesNotMatch(TagPattern, tag);

    [Theory]
    [InlineData("v0.2.0.1")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0+build")]
    [InlineData("0.2.0")]
    [InlineData("release-0.2.0")]
    [InlineData("v0.2.0 ")]
    public void Nothing_else_does_either(string tag) =>
        Assert.DoesNotMatch(TagPattern, tag);

    /// <summary>
    /// Reads the tag pattern out of the workflow, so this cannot pass against a copy that has drifted
    /// from what actually runs.
    /// </summary>
    private static string ExtractPattern()
    {
        var workflow = Path.Combine(RepositoryRoot(), ".github", "workflows", "build.yml");
        Assert.True(File.Exists(workflow), $"Workflow not found at {workflow}");

        var text = File.ReadAllText(workflow);

        // The line reads:  if ! printf '%s' "$REF_NAME" | grep -Eq '<pattern>'; then
        var match = Regex.Match(text, @"REF_NAME"" \| grep -Eq '([^']+)'");
        Assert.True(match.Success, "Could not find the tag pattern in build.yml");

        return match.Groups[1].Value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".github")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
