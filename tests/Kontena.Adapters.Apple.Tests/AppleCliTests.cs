using Kontena.Sdk.Errors;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Adapters.Apple.Tests;

/// <summary>
/// <see cref="AppleCli"/>'s error mapping. Every exit code this CLI gives is 1 — for a missing
/// container, for a stopped apiserver, for anything — so the complaint text is the only signal there
/// is, and reading it is the whole job of this class.
/// <para>
/// The two "not found" strings below are verbatim from <c>container</c> 1.2.2; the second is why the
/// match is on the words rather than on either sentence in full.
/// </para>
/// </summary>
public sealed class AppleCliTests
{
    private static AppleCli Cli(FakeToolRunner runner) => new(runner);

    private static FakeToolRunner Failing(string complaint) =>
        new FakeToolRunner()
            .Install(AppleTool.Definition)
            .When(_ => true, exitCode: 1, errorOutput: [complaint]);

    [Theory]
    [InlineData("Error: container not found: nope")]
    [InlineData("Error: internalError: \"failed to stop container\" (cause: \"notFound: \"container with ID nope not found\"\")")]
    public async Task A_missing_resource_is_reported_as_missing_in_both_shapes(string complaint)
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            async () => await Cli(Failing(complaint)).RunAsync(default, "inspect", "nope"));
    }

    /// <summary>
    /// The apiserver is a launchd service someone may simply not have started. That is unreachable, not
    /// a broken command — the switcher shows a different row for each.
    /// </summary>
    [Fact]
    public async Task An_unreachable_apiserver_is_reported_as_unreachable()
    {
        var runner = Failing("Error: failed to connect to container-apiserver");

        await Assert.ThrowsAsync<EngineUnreachableException>(
            async () => await Cli(runner).RunAsync(default, "list"));
    }

    /// <summary>Anything else still has to arrive as the SDK's own error type, with the CLI's complaint
    /// carried through — an adapter that swallows the reason makes its own message the bug report.</summary>
    [Fact]
    public async Task Any_other_failure_keeps_the_complaint()
    {
        var runner = Failing("Error: something else entirely");

        var error = await Assert.ThrowsAsync<EngineException>(
            async () => await Cli(runner).RunAsync(default, "list"));

        Assert.Contains("something else entirely", error.Message);
    }

    /// <summary>
    /// Output this adapter cannot read must fail rather than come back as an empty list: "no containers"
    /// about a machine that has them is a lie the UI cannot detect.
    /// </summary>
    [Fact]
    public void Unreadable_output_fails_instead_of_becoming_an_empty_list()
    {
        Assert.Throws<EngineException>(
            () => AppleCli.Parse<AppleContainer>("{ not json", "container list"));
    }

    /// <summary>
    /// Empty output is a real answer from at least one command on a fresh install, so it is nothing —
    /// not a parse error.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void Empty_output_is_nothing(string stdout)
    {
        Assert.Empty(AppleCli.Parse<AppleContainer>(stdout, "container list"));
    }
}
