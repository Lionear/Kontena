using System.Diagnostics;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A screenshot capture leaves the user's data directory alone (KON-419).
/// <para>
/// The tool once isolated itself by pointing <c>XDG_CONFIG_HOME</c> and <c>APPDATA</c> at a temp dir,
/// which moves nothing on macOS — and every capture there wrote over the real settings.json, costing
/// every kubeconfig path, remote engine and registry in it. So this runs the real tool, as its own
/// process, under a home of its own with a settings file already in the data directory, and checks
/// that nothing under it changed. Only a separate process can show this: which directory counts as
/// "the user's" is decided once per process, from its environment.
/// </para>
/// <para>
/// On Linux and macOS the fake home is where that directory resolves; on Windows it resolves through
/// the known-folder API, which no environment reaches, so there the test only shows that the scene
/// renders.
/// </para>
/// </summary>
public sealed class ScreenshotSandboxTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("kontena-fake-home-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception)
        {
            // A directory left under the temp dir is not worth failing a test run over.
        }
    }

    // containers is the plainest scene — the tool writes its settings for every scene. settings-tools
    // asks for the latest kubectl and kind and caches the answer under the data directory, when there
    // is a network to ask.
    [Theory]
    [InlineData("containers")]
    [InlineData("settings-tools")]
    public void A_capture_leaves_the_data_directory_alone(string scene)
    {
        // Both platforms' application-data folders and both build flavours' directories in them: the
        // tool may be built either way. The folders must exist up front, because .NET answers "" for
        // a missing one and the data directory then lands relative to the working directory.
        foreach (var appData in new[] { Path.Combine(_home, ".config"), Path.Combine(_home, "Library", "Application Support") })
        {
            foreach (var name in new[] { "Kontena", "Kontena-Dev" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(appData, "Lionear", name)).FullName;
                File.WriteAllText(Path.Combine(directory, "settings.json"), """{ "KubeconfigPaths": [ "/home/me/.kube/prod" ] }""");
            }
        }

        var before = Snapshot();

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList =
            {
                Path.Combine(AppContext.BaseDirectory, "Kontena.Screenshots.dll"),
                "--scene", scene,
                "--out", Path.Combine(_home, "shot.png"),
            },
            WorkingDirectory = _home,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["HOME"] = _home;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_home, ".config");

        using var tool = Process.Start(start)!;
        var output = tool.StandardOutput.ReadToEndAsync();
        var error = tool.StandardError.ReadToEndAsync();
        Assert.True(tool.WaitForExit(TimeSpan.FromMinutes(2)), "the capture did not finish");

        Assert.True(tool.ExitCode == 0, $"the capture failed ({tool.ExitCode}): {output.Result}{error.Result}");
        Assert.True(File.Exists(Path.Combine(_home, "shot.png")), "the capture wrote no image");
        Assert.Equal(before, Snapshot());
    }

    /// <summary>Everything Kontena owns under the fake home, file contents included.</summary>
    private string Snapshot() => string.Join('\n',
        Directory.EnumerateFileSystemEntries(_home, "*", SearchOption.AllDirectories)
            .Where(path => path.Contains("Lionear", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path) ? $"{path}: {File.ReadAllText(path)}" : path));
}
