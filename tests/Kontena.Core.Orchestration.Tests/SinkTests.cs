using Kontena.Core.Orchestration.Export;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The sinks (KON-211) write into somebody's GitOps repository, so these tests are about the two
/// ways that goes wrong: landing somewhere other than where the caller said, and changing a file
/// nobody asked to change. Real files in a temp directory — a mocked file system would only prove
/// the mock agrees with itself — but a faked <c>kustomize</c>, so the "the tool is missing" case is
/// the same test on every machine instead of the one that happens not to have it installed.
/// </summary>
public class SinkTests : IDisposable
{
    private static readonly string[] AddTheResource = ["edit", "add", "resource", "alerts/checkout-slo.yaml"];

    private readonly string _root = Directory.CreateTempSubdirectory("kontena-sink-").FullName;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is not worth failing over.
        }
    }

    private static ManifestBundle Bundle(string source, string yaml = "kind: PrometheusRule")
        => new() { Yaml = yaml, Source = source };

    // ── FileSink ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_source_names_the_file_and_the_yaml_ends_with_one_newline()
    {
        var result = await new FileSink(_root).WriteAsync(Bundle("checkout-slo", "kind: PrometheusRule\n\n\n"));

        Assert.Equal(SinkOutcome.Written, result.Outcome);
        Assert.True(result.Ok);
        Assert.Equal("checkout-slo.yaml", Path.GetFileName(result.Path));
        Assert.Equal("kind: PrometheusRule\n", await File.ReadAllTextAsync(result.Path));
    }

    [Theory]
    [InlineData("checkout-slo.yaml", "checkout-slo.yaml")]
    [InlineData("Checkout SLO / burn rate", "Checkout-SLO-burn-rate.yaml")]
    [InlineData("../../../etc/passwd", "etc-passwd.yaml")]
    [InlineData("..", "")]
    [InlineData("   ", "")]
    public async Task A_source_is_a_label_not_a_path(string source, string expected)
    {
        var result = await new FileSink(_root).WriteAsync(Bundle(source));

        if (expected.Length == 0)
        {
            Assert.Equal(SinkOutcome.Refused, result.Outcome);
            Assert.Empty(Directory.GetFiles(_root));
            return;
        }

        Assert.Equal(SinkOutcome.Written, result.Outcome);
        Assert.Equal(expected, Path.GetFileName(result.Path));

        // One file, in the directory the sink was given: nothing climbed out of it.
        Assert.Equal(expected, Path.GetFileName(Assert.Single(Directory.GetFiles(_root))));
    }

    [Fact]
    public async Task An_empty_bundle_is_refused_rather_than_written_as_an_empty_file()
    {
        var result = await new FileSink(_root).WriteAsync(Bundle("checkout-slo", "\n  \n"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task An_existing_file_with_other_content_is_left_alone()
    {
        var path = Path.Combine(_root, "checkout-slo.yaml");
        await File.WriteAllTextAsync(path, "# hand-written, do not touch\n");

        var result = await new FileSink(_root).WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Equal("# hand-written, do not touch\n", await File.ReadAllTextAsync(path));
        Assert.Contains("already exists", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwrite_replaces_it()
    {
        var path = Path.Combine(_root, "checkout-slo.yaml");
        await File.WriteAllTextAsync(path, "# hand-written\n");

        var result = await new FileSink(_root) { Overwrite = true }.WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Written, result.Outcome);
        Assert.Equal("kind: PrometheusRule\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Rewriting_the_same_bytes_touches_nothing()
    {
        var sink = new FileSink(_root);
        var first = await sink.WriteAsync(Bundle("checkout-slo"));
        var stamp = File.GetLastWriteTimeUtc(first.Path);

        var again = await sink.WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Written, again.Outcome);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first.Path));
    }

    [SkippableFact]
    public async Task It_will_not_write_through_a_symlink()
    {
        var outside = Path.Combine(_root, "outside.yaml");
        await File.WriteAllTextAsync(outside, "# somewhere else\n");
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        SkipUnlessLinkable(() => File.CreateSymbolicLink(Path.Combine(repo, "checkout-slo.yaml"), outside));

        var result = await new FileSink(repo) { Overwrite = true }.WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Equal("# somewhere else\n", await File.ReadAllTextAsync(outside));
        Assert.Contains("symbolic link", result.Message, StringComparison.Ordinal);
    }

    // ── KustomizeSink ────────────────────────────────────────────────────────

    /// <summary>An overlay with a hand-written kustomization, plus the <c>alerts/</c> it lists into.</summary>
    private (string Root, string Alerts, string Kustomization) Overlay()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "clusters", "prod")).FullName;
        var kustomization = Path.Combine(root, "kustomization.yaml");
        File.WriteAllText(
            kustomization,
            "apiVersion: kustomize.config.k8s.io/v1beta1\n"
            + "kind: Kustomization\n\n"
            + "# the workloads, in the order they were added\n"
            + "resources:\n"
            + "  - deployment.yaml\n");

        return (root, Path.Combine(root, "alerts"), kustomization);
    }

    [Fact]
    public async Task It_writes_the_file_and_asks_kustomize_to_list_it()
    {
        var (root, alerts, kustomization) = Overlay();
        var runner = new FakeToolRunner().Install(KnownTools.Kustomize, "v5.4.2");

        var result = await new KustomizeSink(new FileSink(alerts), root, runner)
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Registered, result.Outcome);
        Assert.True(File.Exists(result.Path));

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(AddTheResource, invocation.Arguments);
        Assert.Equal(Overlaid(result.Path), invocation.WorkingDirectory);

        // Kontena never edits the kustomization itself — kustomize was asked, and the fake runs
        // nothing, so the file is exactly as it was written above.
        Assert.Contains("# the workloads", await File.ReadAllTextAsync(kustomization), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_is_reproducible_in_a_terminal()
    {
        var (root, alerts, _) = Overlay();

        var result = await new KustomizeSink(new FileSink(alerts), root, new FakeToolRunner())
            .WriteAsync(Bundle("checkout-slo"));

        // `kustomize edit` works on the kustomization in the current directory, so the cd is part of
        // the command rather than context the reader has to supply.
        Assert.StartsWith("cd ", result.Command, StringComparison.Ordinal);
        Assert.Contains(Overlaid(result.Path), result.Command, StringComparison.Ordinal);
        Assert.EndsWith(
            " && kustomize edit add resource alerts/checkout-slo.yaml",
            result.Command,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay directory as it really is, read back from where the file landed. A temp directory
    /// is reached through a symlink on macOS, so the path handed to the sink and the path it resolved
    /// to are not the same string — and the resolved one is what it reports.
    /// </summary>
    private static string Overlaid(string writtenPath)
        => Path.GetDirectoryName(Path.GetDirectoryName(writtenPath))!;

    [Fact]
    public async Task Without_kustomize_the_file_is_written_and_the_kustomization_is_byte_identical()
    {
        var (root, alerts, kustomization) = Overlay();
        var before = await File.ReadAllBytesAsync(kustomization);

        // Nothing installed: the fake throws ToolNotFoundException, as the real runner does.
        var result = await new KustomizeSink(new FileSink(alerts), root, new FakeToolRunner())
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.NotRegistered, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal("kind: PrometheusRule\n", await File.ReadAllTextAsync(result.Path));
        Assert.Equal(before, await File.ReadAllBytesAsync(kustomization));

        // Relative to the kustomization, indented, ready to paste under `resources:`.
        Assert.Equal("  - alerts/checkout-slo.yaml", result.FallbackLine);
        Assert.Contains("not installed", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            Path.Combine(Overlaid(result.Path), "kustomization.yaml"),
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kustomize_that_refuses_says_so_in_its_own_words()
    {
        var (root, alerts, kustomization) = Overlay();
        var before = await File.ReadAllBytesAsync(kustomization);
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kustomize, "v5.4.2")
            .When(i => i.Arguments.Contains("resource"), errorOutput: ["must build at directory"], exitCode: 1);

        var result = await new KustomizeSink(new FileSink(alerts), root, runner)
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.NotRegistered, result.Outcome);
        Assert.Contains("must build at directory", result.Message, StringComparison.Ordinal);
        Assert.Equal("  - alerts/checkout-slo.yaml", result.FallbackLine);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(before, await File.ReadAllBytesAsync(kustomization));
    }

    [Fact]
    public async Task A_directory_without_a_kustomization_is_refused_before_anything_is_written()
    {
        var alerts = Path.Combine(_root, "alerts");

        var result = await new KustomizeSink(new FileSink(alerts), _root, new FakeToolRunner())
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Contains("no kustomization.yaml", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(alerts));
    }

    [Fact]
    public async Task A_target_outside_the_kustomization_is_refused_before_anything_is_written()
    {
        var (root, _, _) = Overlay();
        var elsewhere = Path.Combine(_root, "elsewhere");

        var result = await new KustomizeSink(new FileSink(elsewhere), root, new FakeToolRunner())
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Contains("outside", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(elsewhere));
    }

    [SkippableFact]
    public async Task A_directory_that_is_a_link_out_of_the_overlay_is_refused_too()
    {
        var (root, alerts, _) = Overlay();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        SkipUnlessLinkable(() => Directory.CreateSymbolicLink(alerts, elsewhere));

        var result = await new KustomizeSink(new FileSink(alerts), root, new FakeToolRunner())
            .WriteAsync(Bundle("checkout-slo"));

        Assert.Equal(SinkOutcome.Refused, result.Outcome);
        Assert.Contains("outside", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    /// <summary>
    /// Creating a symlink needs Developer Mode or an elevated shell on Windows. Skip there rather
    /// than fail: the check being tested is the same everywhere, the ability to set it up is not.
    /// </summary>
    private static void SkipUnlessLinkable(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, $"This machine will not create symbolic links: {ex.Message}");
        }
    }
}
