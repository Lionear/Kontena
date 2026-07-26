using Kontena.Core.Tooling;

namespace Kontena.Core.Tests;

/// <summary>
/// Finding a tool (KON-129). The interesting part is that PATH alone is not enough: a desktop app
/// launched from Finder or the Dock inherits a minimal environment, so the directories Homebrew
/// installs to are missing even though the tools are there.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class ToolLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"kontena-tools-{Guid.NewGuid():N}");

    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public ToolLocatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Plant(string name)
    {
        var path = Path.Combine(_directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Finds_an_executable_on_PATH()
    {
        var planted = Plant("kontena-probe");
        Environment.SetEnvironmentVariable("PATH", _directory);

        Assert.Equal(planted, ToolLocator.Locate("kontena-probe"));
    }

    [Fact]
    public void Finds_an_executable_in_an_extra_search_path_when_PATH_does_not_have_it()
    {
        // This is the macOS case: the tool is installed, PATH just does not mention where.
        var planted = Plant("kontena-probe");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.Equal(planted, ToolLocator.Locate("kontena-probe", [_directory]));
    }

    [Fact]
    public void PATH_wins_over_the_extra_paths()
    {
        // If someone arranged for a particular copy to be first, that is the one they mean.
        var onPath = Plant("kontena-probe");

        var other = Path.Combine(_directory, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, OperatingSystem.IsWindows() ? "kontena-probe.exe" : "kontena-probe"), "");

        Environment.SetEnvironmentVariable("PATH", _directory);

        Assert.Equal(onPath, ToolLocator.Locate("kontena-probe", [other]));
    }

    [Fact]
    public void An_absolute_path_is_answered_not_searched()
    {
        var planted = Plant("kontena-probe");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.Equal(planted, ToolLocator.Locate(planted));
        Assert.Null(ToolLocator.Locate(Path.Combine(_directory, "not-there")));
    }

    [Fact]
    public void Returns_null_when_it_is_nowhere()
    {
        Environment.SetEnvironmentVariable("PATH", _directory);

        Assert.Null(ToolLocator.Locate("kontena-definitely-not-installed"));
    }

    [Fact]
    public void Default_search_paths_are_absolute_and_distinct()
    {
        var paths = ToolLocator.DefaultSearchPaths();

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.True(Path.IsPathRooted(p), $"{p} is not absolute"));
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
    }
}
