using Kontena.Adapters.Docker;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// Build and compose run the docker or podman CLI directly, not through ToolRunner (KON-487). Started
/// from Finder or the Dock, Kontena has launchd's bare PATH, and the CLI goes looking for its own
/// helpers on it — <c>docker-credential-desktop</c> on every pull. The bare PATH is handed in
/// explicitly: a test runner started from a terminal already has the full one, the one case the bug
/// cannot happen in.
/// </summary>
public class CliPathTests
{
    [Fact]
    public async Task On_macos_the_cli_started_with_a_bare_path_still_sees_homebrew()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };

        var lines = new List<string>();
        await foreach (var line in DockerEngine.RunCliAsync("sh", ["-c", "echo \"$PATH\""], null, env, "test", default))
            lines.Add(line.Text);

        var directories = Assert.Single(lines).Trim().Split(':');

        Assert.Contains("/opt/homebrew/bin", directories);
        Assert.Contains("/usr/local/bin", directories);
        Assert.Equal("/usr/bin", directories[0]);
    }

    [Fact]
    public async Task A_cli_that_is_nowhere_to_be_found_says_so()
    {
        var lines = new List<string>();
        await foreach (var line in DockerEngine.RunCliAsync("kontena-no-such-cli", [], null, null, "build", default))
            lines.Add(line.Text);

        Assert.StartsWith(
            "Could not start 'kontena-no-such-cli build' — is the kontena-no-such-cli CLI installed and on PATH?",
            Assert.Single(lines));
    }
}
