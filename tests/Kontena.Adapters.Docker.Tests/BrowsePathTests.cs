using Kontena.Adapters.Docker;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// The path a browse request is turned into. This runs without an engine because it is a rule, not a
/// call — and it is the rule that keeps a listing inside the volume: the archive endpoint would happily
/// read the holder container's own filesystem if a path were allowed to climb out of the mount.
/// </summary>
public class BrowsePathTests
{
    [Theory]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData("/data", "/data")]
    [InlineData("data", "/data")]
    [InlineData("/data/", "/data")]
    [InlineData("/data//base", "/data/base")]
    [InlineData("/data/./base", "/data/base")]
    public void Normalizes_to_an_absolute_path_without_noise(string input, string expected) =>
        Assert.Equal(expected, DockerEngine.NormalizeBrowsePath(input));

    [Theory]
    [InlineData("/..", "")]
    [InlineData("../../etc", "/etc")]
    [InlineData("/data/../..", "")]
    [InlineData("/data/../../../etc/passwd", "/etc/passwd")]
    [InlineData("/data/sub/../other", "/data/other")]
    public void Cannot_climb_out_of_the_volume(string input, string expected)
    {
        // Resolved here rather than passed on: whatever comes back is still rooted at the mount point,
        // so "/etc/passwd" means the volume's own /etc/passwd — not the container's.
        var result = DockerEngine.NormalizeBrowsePath(input);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("..", result, StringComparison.Ordinal);
    }
}
