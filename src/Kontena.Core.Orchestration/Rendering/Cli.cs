using Kontena.Sdk.Tooling;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>What running an external tool produced: exit code plus both streams.</summary>
internal readonly record struct CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Whatever the tool said about failing — stderr, or stdout when stderr is empty.</summary>
    public string Complaint => StdErr.Length > 0 ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>
/// The renderers' view of running a tool: they already hold a resolved path, so they keep a
/// path-shaped call.
/// </summary>
/// <remarks>
/// Everything below this line is <see cref="Kontena.Sdk.Tooling"/> (KON-129). Process handling used
/// to live here in full, which was fine while rendering was the only thing shelling out — but cluster
/// provisioning, the metrics install and the engine install-assist all need the same three steps, and
/// the second copy is where they start to differ. One implementation, two shapes of call.
/// </remarks>
internal static class Cli
{
    private static readonly ToolRunner Runner = new();

    public static async Task<CliResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var result = await Runner.RunAsync(
            new ToolInvocation(Describing(exe), args) { WorkingDirectory = workingDirectory }, ct);

        return new CliResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    /// <summary>The absolute path of <paramref name="exe"/>, or null when it isn't installed.</summary>
    public static string? Locate(string exe) => ToolLocator.Locate(exe);

    /// <summary>Render an invocation the way a user would type it, so a render can be reproduced.</summary>
    public static string Describe(string exe, IReadOnlyList<string> args) => ToolCommand.Describe(exe, args);

    /// <summary>
    /// A resolved path in <see cref="ExternalTool"/> clothing. No install hints: by the time a
    /// renderer runs something it has already established the tool is there, and a renderer is the
    /// wrong place to advise on installing one.
    /// </summary>
    private static ExternalTool Describing(string exe)
        => new(Path.GetFileNameWithoutExtension(exe), exe, [], []);
}
