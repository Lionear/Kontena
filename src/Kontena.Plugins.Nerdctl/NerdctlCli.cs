using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// The one place that shells out to nerdctl (KON-141). nerdctl has no daemon socket to talk to and no
/// notion of a "current" namespace the way <c>docker context</c> does — every command needs
/// <c>--namespace &lt;ns&gt;</c> to look at the right containerd namespace, and a command built without
/// it does not fail loudly: it prints an empty list while dozens of containers run in <c>k8s.io</c>.
/// Routing every invocation through this one seam is what makes forgetting the namespace impossible
/// instead of merely unlikely — nothing else in the plugin builds a <see cref="ToolInvocation"/> or
/// touches <see cref="IToolRunner"/> directly.
/// </summary>
public sealed class NerdctlCli(IToolRunner runner, string @namespace)
{
    /// <summary>
    /// Runs a short, quiet nerdctl command to completion and returns its stdout — for listing commands,
    /// not logs. <see cref="StreamAsync"/> is for anything a user would watch progress on.
    /// </summary>
    /// <exception cref="ToolNotFoundException">nerdctl is not installed. Passed through unchanged: the
    /// engine layer decides how a missing binary is reported, not this layer.</exception>
    /// <exception cref="ToolFailedException">nerdctl ran and exited non-zero.</exception>
    public async ValueTask<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var invocation = Invocation(args);
        var result = await runner.RunAsync(invocation, ct).ConfigureAwait(false);

        if (!result.Ok)
            throw new ToolFailedException(invocation.CommandLine, result.ExitCode, result.Complaint);

        return result.StandardOutput;
    }

    /// <summary>
    /// Runs a nerdctl command and yields its output as it arrives — for <c>logs</c> and anything else
    /// slow enough that buffered output would look indistinguishable from a hang.
    /// </summary>
    /// <exception cref="ToolNotFoundException">nerdctl is not installed. Passed through unchanged, same
    /// as <see cref="RunAsync"/>.</exception>
    /// <exception cref="ToolFailedException">nerdctl ran and exited non-zero.</exception>
    public IAsyncEnumerable<ToolLine> StreamAsync(CancellationToken ct, params string[] args) =>
        runner.StreamAsync(Invocation(args), ct);

    /// <summary>Prepends <c>--namespace &lt;ns&gt;</c> ahead of the subcommand — see the type doc for why
    /// this is the only place that does so.</summary>
    private ToolInvocation Invocation(string[] args) =>
        new(NerdctlTool.Definition, ["--namespace", @namespace, .. args]);
}
