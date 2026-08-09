using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// Builds and volume browsing — the last two stages of KON-31. The build output asserted here is
/// BuildKit's, as this CLI printed it; the listing lines are what <c>stat -c</c> produced inside a real
/// throwaway container against a real ext4 volume.
/// </summary>
public sealed class AppleEngineBuildBrowseTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(AppleTool.Definition);

    private static AppleEngine Engine(IToolRunner runner) =>
        new(new AppleCli(runner), "apple", "Apple container");

    private static BuildRequest Request(string context) => new() { ContextPath = context, Tag = "app:v1" };

    // ── Build ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildImageAsync_reports_the_builder_output_then_says_it_finished()
    {
        var runner = Installed().When(_ => true, errorOutput: [
            "#1 [internal] load build definition from Dockerfile",
            "#1 DONE 0.1s",
            "#5 [linux/arm64 1/2] RUN echo hi",
            "#5 DONE 0.4s",
        ]);

        var progress = await Engine(runner).BuildImageAsync(Request(Directory.GetCurrentDirectory())).ToListAsync();

        Assert.All(progress, p => Assert.Null(p.Error));
        Assert.Contains(progress, p => p.Text.Contains("RUN echo hi", StringComparison.Ordinal));
        Assert.Equal("Built app:v1", progress[^1].Text);
    }

    /// <summary>
    /// A failed build belongs in the build console, not in an exception: the caller has already printed
    /// twenty lines of it by the time the last one says why it stopped.
    /// </summary>
    [Fact]
    public async Task BuildImageAsync_ends_with_the_failure_rather_than_throwing()
    {
        var runner = Installed().When(
            _ => true,
            exitCode: 1,
            errorOutput: ["#5 ERROR: process \"/bin/sh -c exit 42\" did not complete successfully"]);

        var progress = await Engine(runner).BuildImageAsync(Request(Directory.GetCurrentDirectory())).ToListAsync();

        var last = progress[^1];
        Assert.NotNull(last.Error);
        Assert.Contains("exit 42", last.Error);

        // And it must not claim success afterwards.
        Assert.DoesNotContain(progress, p => p.Text.StartsWith("Built ", StringComparison.Ordinal));
    }

    /// <summary>A context that is not there is worth saying before starting a builder for it.</summary>
    [Fact]
    public async Task BuildImageAsync_reports_a_missing_context_without_running_anything()
    {
        var runner = Installed();

        var progress = await Engine(runner)
            .BuildImageAsync(Request(Path.Combine(Path.GetTempPath(), "kon31-no-such-context")))
            .ToListAsync();

        Assert.NotNull(Assert.Single(progress).Error);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// Both flags exist on this CLI, so a request carrying them must pass them on — a dropped
    /// <c>--target</c> builds a different stage than the one asked for, silently.
    /// </summary>
    [Fact]
    public async Task BuildImageAsync_passes_every_flag_the_request_carries()
    {
        var runner = Installed().When(_ => true, errorOutput: ["#1 DONE"]);
        var context = Directory.GetCurrentDirectory();

        await Engine(runner).BuildImageAsync(new BuildRequest
        {
            ContextPath = context,
            Tag = "app:v1",
            Target = "runtime",
            NoCache = true,
            Pull = true,
            BuildArgs = new Dictionary<string, string> { ["GREETING"] = "hoi" },
        }).ToListAsync();

        var arguments = Assert.Single(runner.Invocations).Arguments;
        Assert.Contains("--progress", arguments);
        Assert.Contains("plain", arguments);
        Assert.Contains("--target", arguments);
        Assert.Contains("runtime", arguments);
        Assert.Contains("--no-cache", arguments);
        Assert.Contains("--pull", arguments);
        Assert.Contains("GREETING=hoi", arguments);

        // The context is the last word, as the CLI expects.
        Assert.Equal(context, arguments[^1]);
    }

    // ── Browse ──────────────────────────────────────────────────────────────

    private const string Listing =
        """
        directory|4096|1786290441|/kontena-volume/lost+found
        directory|4096|1786290442|/kontena-volume/sub
        regular file|6|1786290442|/kontena-volume/file.txt
        regular file|7168|1786290442|/kontena-volume/big.bin
        symbolic link|13|1786290442|/kontena-volume/link
        """;

    private static FakeToolRunner Browsing() =>
        Installed()
            .When(i => i.Arguments[0] == "run", output: [Listing])
            .When(i => i.Arguments[0] == "image",
                output: ["""[{"id":"a","configuration":{"name":"alpine:3.20"},"variants":[{"size":4093973,"platform":{"architecture":"arm64","os":"linux"}}]}]"""])
            .When(_ => true, output: ["[]"]);

    [Fact]
    public async Task BrowseVolumeAsync_reads_type_size_and_time_off_each_entry()
    {
        var listing = await Engine(Browsing()).BrowseVolumeAsync("data");

        var directory = Assert.Single(listing.Entries, e => e.Name == "sub");
        Assert.True(directory.IsDirectory);

        var file = Assert.Single(listing.Entries, e => e.Name == "big.bin");
        Assert.False(file.IsDirectory);
        Assert.Equal(7168, file.SizeBytes);
        Assert.NotNull(file.ModifiedAt);
    }

    /// <summary>
    /// Volumes here are ext4 images, and every ext4 filesystem carries a <c>lost+found</c> at its root
    /// that nobody created. Showing it would make every fresh volume look like it has something in it.
    /// </summary>
    [Fact]
    public async Task BrowseVolumeAsync_hides_the_lost_and_found_the_filesystem_made()
    {
        var listing = await Engine(Browsing()).BrowseVolumeAsync("data");

        Assert.DoesNotContain(listing.Entries, e => e.Name == "lost+found");
        Assert.Equal(4, listing.Entries.Count);
    }

    /// <summary>A symlink is not a directory here even when it points at one: following it leads out of
    /// the mount.</summary>
    [Fact]
    public async Task BrowseVolumeAsync_does_not_treat_a_symlink_as_a_directory()
    {
        var listing = await Engine(Browsing()).BrowseVolumeAsync("data");

        Assert.False(Assert.Single(listing.Entries, e => e.Name == "link").IsDirectory);
    }

    /// <summary>
    /// The command runs inside a container whose own filesystem sits just outside the mount, so a path
    /// climbing out of it has to be resolved here rather than passed on.
    /// </summary>
    [Theory]
    [InlineData("/", "")]
    [InlineData("/sub", "/sub")]
    [InlineData("sub/deeper/", "/sub/deeper")]
    [InlineData("/sub/../other", "/other")]
    [InlineData("/../../etc", "/etc")]
    [InlineData("/..", "")]
    [InlineData("/./sub", "/sub")]
    public void NormalizeBrowsePath_cannot_leave_the_mount(string input, string expected)
    {
        Assert.Equal(expected, AppleEngine.NormalizeBrowsePath(input));
    }

    /// <summary>The volume is mounted, and it is the image that provides the shell doing the looking.</summary>
    [Fact]
    public async Task BrowseVolumeAsync_mounts_the_volume_into_a_throwaway_container()
    {
        var runner = Browsing();

        await Engine(runner).BrowseVolumeAsync("data");

        var run = runner.Invocations.Single(i => i.Arguments[0] == "run").Arguments;
        Assert.Contains("--rm", run);
        Assert.Contains("data:/kontena-volume", run);
        Assert.Contains("alpine:3.20", run);
    }

    /// <summary>
    /// A path that is not there is an ordinary thing — a folder opened after it was deleted — and the
    /// answer is one line. Without this it arrives as the CLI's whole stderr, which on this runtime
    /// begins with eight lines of a virtual machine booting.
    /// </summary>
    [Fact]
    public async Task BrowseVolumeAsync_says_a_missing_path_is_missing()
    {
        var runner = Installed()
            .When(i => i.Arguments[0] == "image",
                output: ["""[{"id":"a","configuration":{"name":"alpine:3.20"},"variants":[{"size":1,"platform":{"architecture":"arm64","os":"linux"}}]}]"""])
            .When(i => i.Arguments[0] == "run", exitCode: 1, errorOutput: [
                "[6/6] Starting container [0s]",
                "find: /kontena-volume/gone: No such file or directory",
            ])
            .When(_ => true, output: ["[]"]);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(
            async () => await Engine(runner).BrowseVolumeAsync("data", "/gone"));

        Assert.Contains("/gone", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting container", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to mount the volume into is a real state on a fresh install, and the advice that fixes it
    /// is one line — better than an error about a container that could not be created.
    /// </summary>
    [Fact]
    public async Task BrowseVolumeAsync_explains_when_there_is_no_image_to_mount_into()
    {
        var runner = Installed().When(_ => true, output: ["[]"]);

        var error = await Assert.ThrowsAsync<EngineException>(
            async () => await Engine(runner).BrowseVolumeAsync("data"));

        Assert.Contains("Pull any image first", error.Message, StringComparison.Ordinal);
    }
}
