using Kontena.Sdk.Errors;
using Kontena.Sdk.Tooling;
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

    /// <summary>
    /// Both shapes a stopped service takes, verbatim from 1.2.2: the XPC error every command gives, and
    /// the plain sentence <c>system status</c> prints on stdout. The second is why the check is not on
    /// "XPC" alone — <c>system status</c> is the ping, so it is the first command to hit a stopped
    /// service and the one whose failure a user sees first.
    /// </summary>
    public static TheoryData<string> ServiceDownComplaints =>
    [
        "Error: internalError: \"failed to list containers\" (cause: \"interrupted: \"XPC connection error: Connection invalid\"\")",
        "apiserver is not running and not registered with launchd",
    ];

    /// <summary>
    /// A stopped service is Kontena's job, not the user's: starting it takes a couple of seconds in the
    /// user's own launchd domain, with no password to type. So the command that tripped over it is
    /// simply run again, and the user never learns anything happened.
    /// </summary>
    [Theory]
    [MemberData(nameof(ServiceDownComplaints))]
    public async Task A_stopped_service_is_started_and_the_command_tried_again(string complaint)
    {
        var runner = new FakeToolRunner().Install(AppleTool.Definition);
        runner.When(
            // Everything fails until the service has been started — including the start itself, whose
            // own invocation is already recorded by the time this runs, so it is not caught by its own
            // script.
            invocation => !runner.Invocations.Any(IsStart),
            exitCode: 1,
            errorOutput: [complaint]);
        runner.When(IsList, output: ["[]"]);

        var stdout = await Cli(runner).RunAsync(default, "list", "--format", "json");

        Assert.Equal("[]", stdout);
        Assert.Equal(
            ["container list --format json", "container system start --disable-kernel-install", "container list --format json"],
            runner.Invocations.Select(i => i.CommandLine));
    }

    /// <summary>
    /// When the service cannot be started — no kernel installed, since the prompt to install one is
    /// declined — the CLI's own complaint has to reach the user, manual command and all. And it is tried
    /// exactly once: a loop that keeps restarting would turn a service that structurally refuses to run
    /// into a hang.
    /// </summary>
    [Fact]
    public async Task A_service_that_will_not_start_surfaces_the_original_failure_without_looping()
    {
        var runner = Failing("Error: internalError: \"failed to list containers\" (cause: \"interrupted: \"XPC connection error: Connection invalid\"\")");

        await Assert.ThrowsAsync<EngineUnreachableException>(
            async () => await Cli(runner).RunAsync(default, "list"));

        Assert.Equal(
            ["container list", "container system start --disable-kernel-install"],
            runner.Invocations.Select(i => i.CommandLine));
    }

    private static bool IsStart(ToolInvocation invocation) =>
        invocation.Arguments is ["system", "start", ..];

    private static bool IsList(ToolInvocation invocation) =>
        invocation.Arguments is ["list", ..];

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
