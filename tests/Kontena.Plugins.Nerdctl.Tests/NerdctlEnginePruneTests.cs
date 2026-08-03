using Kontena.Sdk.Errors;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlEngine"/>'s prune methods (KON-141 PR 3 task 4). nerdctl's prune output is not
/// JSON: a header per resource kind, then either bare names/ids (containers, volumes) or
/// <c>Untagged:</c>/<c>deleted:</c> pairs (images) — and nothing at all when there was nothing to prune
/// (Notes/nerdctl-write-formats.md). What discriminates a correct implementation from one that never ran
/// is the removed-item count, not the empty case: every method's default result already looks like "zero
/// removed", so the empty-output tests below only prove the shape is handled, while the populated ones
/// prove the count is read off the right lines — in particular that an image's <c>Untagged:</c> and
/// <c>deleted:</c> lines are not counted as two removals of one image.
/// <para>
/// <see cref="PruneResult.SpaceReclaimedBytes"/> is asserted at 0 (its default) everywhere here: nerdctl
/// prints no "Total reclaimed space" line the way Docker does, so there is nothing honest to compute it
/// from (see the production XML docs for why inventing one from the `deleted:` lines would be wrong).
/// </para>
/// </summary>
public sealed class NerdctlEnginePruneTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    // ── PruneContainersAsync ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneContainersAsync_runs_container_prune_dash_f()
    {
        var runner = Installed().When(_ => true, output: []);

        await Engine(runner).PruneContainersAsync();

        Assert.Equal(["--namespace", "k8s.io", "container", "prune", "-f"], runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PruneContainersAsync_returns_an_empty_result_when_nothing_was_pruned()
    {
        // A real capture against nerdctl 2.3.5 shows a node with no stopped containers prints nothing at
        // all — not even the "Deleted Containers:" header.
        var runner = Installed().When(_ => true, output: []);

        var result = await Engine(runner).PruneContainersAsync();

        Assert.Equal(0, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneContainersAsync_counts_the_ids_removed_under_the_header()
    {
        var runner = Installed().When(_ => true, output: [
            "Deleted Containers:",
            "a1b2c3d4e5f6",
            "1a2b3c4d5e6f",
        ]);

        var result = await Engine(runner).PruneContainersAsync();

        Assert.Equal(2, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneContainersAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).PruneContainersAsync().AsTask());
    }

    [Fact]
    public async Task PruneContainersAsync_for_a_generic_failure_throws_EngineException_with_nerdctls_message()
    {
        var runner = Installed().When(_ => true, errorOutput: ["something went wrong"], exitCode: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(
            () => Engine(runner).PruneContainersAsync().AsTask());

        Assert.IsNotType<ResourceNotFoundException>(ex);
        Assert.Contains("something went wrong", ex.Message, StringComparison.Ordinal);
    }

    // ── PruneImagesAsync ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneImagesAsync_allUnused_true_adds_dash_dash_all()
    {
        var runner = Installed().When(_ => true, output: []);

        await Engine(runner).PruneImagesAsync(allUnused: true);

        Assert.Equal(
            ["--namespace", "k8s.io", "image", "prune", "-f", "--all"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PruneImagesAsync_allUnused_false_omits_dash_dash_all()
    {
        var runner = Installed().When(_ => true, output: []);

        await Engine(runner).PruneImagesAsync(allUnused: false);

        Assert.Equal(
            ["--namespace", "k8s.io", "image", "prune", "-f"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PruneImagesAsync_returns_an_empty_result_when_nothing_was_pruned()
    {
        var runner = Installed().When(_ => true, output: []);

        var result = await Engine(runner).PruneImagesAsync();

        Assert.Equal(0, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneImagesAsync_counts_images_not_their_untagged_and_deleted_lines()
    {
        // Exact shape captured against nerdctl 2.3.5 (Notes/nerdctl-write-formats.md): one image,
        // one "Untagged:" line and two "deleted: sha256:…" layer lines underneath it. A naive line
        // count would read this as 3 (or 4, counting the header) removed items; it is one image.
        var runner = Installed().When(_ => true, output: [
            "Deleted Images:",
            "Untagged: docker.io/library/nginx@sha256:65645c7bb6a0",
            "deleted: sha256:08000c18d16dadf9553d747a58cf44023423a9ab010aab96cf263d2216b8b350",
            "deleted: sha256:d71eae0084c1aa823dd8fb2ecf8604d5c0f4911226c042bb1f8297e819f4b192",
        ]);

        var result = await Engine(runner).PruneImagesAsync();

        Assert.Equal(1, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneImagesAsync_counts_each_image_once_across_multiple_images()
    {
        var runner = Installed().When(_ => true, output: [
            "Deleted Images:",
            "Untagged: docker.io/library/nginx@sha256:65645c7bb6a0",
            "deleted: sha256:08000c18d16dadf9553d747a58cf44023423a9ab010aab96cf263d2216b8b350",
            "Untagged: docker.io/library/alpine@sha256:aaaaaaaaaaaa",
        ]);

        var result = await Engine(runner).PruneImagesAsync();

        Assert.Equal(2, result.ItemsDeleted);
    }

    [Fact]
    public async Task PruneImagesAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).PruneImagesAsync().AsTask());
    }

    // ── PruneVolumesAsync ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneVolumesAsync_runs_volume_prune_dash_f_dash_dash_all()
    {
        // nerdctl's own default (no --all) prunes only anonymous volumes; the CEAL contract promises
        // "all volumes not used by any container", so --all is required, not optional, here.
        var runner = Installed().When(_ => true, output: []);

        await Engine(runner).PruneVolumesAsync();

        Assert.Equal(
            ["--namespace", "k8s.io", "volume", "prune", "-f", "--all"],
            runner.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task PruneVolumesAsync_returns_an_empty_result_when_nothing_was_pruned()
    {
        var runner = Installed().When(_ => true, output: []);

        var result = await Engine(runner).PruneVolumesAsync();

        Assert.Equal(0, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneVolumesAsync_counts_the_names_removed_under_the_header()
    {
        // Exact shape captured against nerdctl 2.3.5 (Notes/nerdctl-write-formats.md).
        var runner = Installed().When(_ => true, output: [
            "Deleted Volumes:",
            "probe-vol",
        ]);

        var result = await Engine(runner).PruneVolumesAsync();

        Assert.Equal(1, result.ItemsDeleted);
        Assert.Equal(0, result.SpaceReclaimedBytes);
    }

    [Fact]
    public async Task PruneVolumesAsync_translates_a_missing_binary_into_the_shared_engine_exception()
    {
        await Assert.ThrowsAsync<EngineUnreachableException>(
            () => Engine(new FakeToolRunner()).PruneVolumesAsync().AsTask());
    }
}
