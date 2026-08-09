using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// Images and registries. The <c>image inspect</c> fixture is a real capture of <c>nginx:alpine</c> —
/// chosen because it declares both an exposed port and a volume, which is how this adapter knows the
/// CLI reports neither.
/// </summary>
public sealed class AppleEngineImageTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    // ── Pull ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every line the pull prints is a status. The byte figures in it are not parsed on purpose: the
    /// separators move with the host's locale and the two halves of the fraction can carry different
    /// units, so a number read wrong is a progress bar that jumps.
    /// </summary>
    [Fact]
    public async Task PullImageAsync_reports_each_line_as_it_arrives()
    {
        var runner = Installed().When(_ => true, errorOutput: [
            "[1/2] Fetching image [0s]",
            "[1/2] Fetching image 47% (64 of 111 blobs, 91,7/191,6 MB, 17,3 MB/s) [10s]",
            "[2/2] Unpacking image for platform linux/arm64/v8 [19s]",
        ]);

        var progress = await Engine(runner).PullImageAsync("nginx:alpine").ToListAsync();

        Assert.Equal(3, progress.Count);
        Assert.All(progress, p => Assert.Equal("nginx:alpine", p.Reference));
        Assert.All(progress, p => Assert.Null(p.Current));
        Assert.All(progress, p => Assert.Null(p.Total));
        Assert.Contains("47%", progress[1].Status);
    }

    /// <summary>The whole pull is narrated on stderr — stdout stays empty for its entire duration, so a
    /// reader that only took stdout would show a silent, frozen pull.</summary>
    [Fact]
    public async Task PullImageAsync_reads_the_stderr_the_cli_narrates_on()
    {
        var runner = Installed().When(_ => true, output: [], errorOutput: ["[1/2] Fetching image [0s]"]);

        Assert.Single(await Engine(runner).PullImageAsync("alpine").ToListAsync());
    }

    /// <summary>
    /// <c>--progress plain</c> so the output shape does not depend on whether the process happened to be
    /// given a terminal.
    /// </summary>
    [Fact]
    public async Task PullImageAsync_asks_for_plain_progress()
    {
        var runner = Installed().When(_ => true, errorOutput: ["x"]);

        await Engine(runner).PullImageAsync("alpine").ToListAsync();

        Assert.Equal(
            ["image", "pull", "--progress", "plain", "alpine"],
            Assert.Single(runner.Invocations).Arguments);
    }

    /// <summary>
    /// A credential cannot be used for one pull: the command takes none, and the only way to supply one
    /// is a login that keeps the secret in the runtime's own store. Refusing beats writing someone's
    /// password somewhere they did not ask for.
    /// </summary>
    [Fact]
    public async Task PullImageAsync_refuses_a_credential_rather_than_storing_it()
    {
        var runner = Installed();

        await Assert.ThrowsAsync<NotSupportedException>(async () => await Engine(runner)
            .PullImageAsync("private/app", new RegistryCredential("registry.io", "user", "secret"))
            .ToListAsync());

        // Nothing ran: the refusal happens before the CLI is touched, so no secret reaches a process.
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task VerifyRegistryLoginAsync_refuses_for_the_same_reason()
    {
        var error = await Assert.ThrowsAsync<NotSupportedException>(async () => await Engine(Installed())
            .VerifyRegistryLoginAsync(new RegistryCredential("registry.io", "user", "secret")));

        Assert.Contains("keychain", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Inspect ─────────────────────────────────────────────────────────────

    /// <summary>The environment is the one thing the image config here actually carries.</summary>
    [Fact]
    public async Task InspectImageAsync_reads_the_environment_of_the_native_variant()
    {
        var runner = Installed().When(_ => true, output: [Fixture("image-inspect-nginx.json")]);

        var config = await Engine(runner).InspectImageAsync("nginx:alpine");

        Assert.NotNull(config);
        Assert.Contains("NGINX_VERSION", config.Environment.Keys);
        Assert.Contains("PATH", config.Environment.Keys);
    }

    /// <summary>
    /// nginx declares an exposed port and a volume, and this CLI reports neither — the keys are absent
    /// from every variant's config. Empty here means the source said nothing, and the Run dialog adds
    /// rows only for what it is given, so nothing on screen claims the image exposes none.
    /// </summary>
    [Fact]
    public async Task InspectImageAsync_has_no_ports_or_volumes_because_the_cli_reports_none()
    {
        var runner = Installed().When(_ => true, output: [Fixture("image-inspect-nginx.json")]);

        var config = await Engine(runner).InspectImageAsync("nginx:alpine");

        Assert.NotNull(config);
        Assert.Empty(config.ExposedPorts);
        Assert.Empty(config.Volumes);
    }

    /// <summary>
    /// "Not here" is an ordinary answer, not an error: the Run flow asks about whatever was typed in the
    /// image box, and the contract's word for that is null.
    /// </summary>
    [Fact]
    public async Task InspectImageAsync_returns_null_for_an_image_that_is_not_present()
    {
        var runner = Installed().When(
            _ => true, exitCode: 1, errorOutput: ["Error: image not found: nosuchimage:v9"]);

        Assert.Null(await Engine(runner).InspectImageAsync("nosuchimage:v9"));
    }

    // ── Tag and remove ──────────────────────────────────────────────────────

    [Fact]
    public async Task TagImageAsync_gives_the_image_a_second_name()
    {
        var runner = Installed();

        await Engine(runner).TagImageAsync("nginx:alpine", "localhost/mine:v1");

        Assert.Equal(
            ["image", "tag", "nginx:alpine", "localhost/mine:v1"],
            Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task RemoveImageAsync_deletes_by_reference()
    {
        var runner = Installed();

        await Engine(runner).RemoveImageAsync("nginx:alpine");

        Assert.Equal(["image", "delete", "nginx:alpine"], Assert.Single(runner.Invocations).Arguments);
    }
}
