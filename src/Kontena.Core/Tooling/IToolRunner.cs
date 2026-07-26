namespace Kontena.Core.Tooling;

/// <summary>
/// Finds and drives the external tools Kontena does not ship (kind, minikube, kubectl, helm, podman).
/// <para>
/// One seam for all of them, because provisioning a cluster, installing a metrics source and the
/// engine install-assist are the same three steps wearing different labels: is it here, how would you
/// get it, and run it while showing what it says.
/// </para>
/// <para>
/// An interface rather than a static helper so the code that drives a cluster can be tested on a
/// machine that has no cluster tooling at all.
/// </para>
/// </summary>
public interface IToolRunner
{
    /// <summary>
    /// Look for <paramref name="tool"/> and ask its version. Never throws for a missing tool — being
    /// absent is an answer, and the caller usually wants to say so rather than fail.
    /// </summary>
    ValueTask<ToolLocation> FindAsync(ExternalTool tool, CancellationToken ct = default);

    /// <summary>
    /// Run to completion and hand back both streams. For short, quiet commands — anything a person
    /// would wait for without wondering whether it hung should use <see cref="StreamAsync"/>.
    /// </summary>
    /// <exception cref="ToolNotFoundException">The tool is not installed.</exception>
    ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct = default);

    /// <summary>
    /// Run and yield output line by line as it arrives, both streams interleaved in arrival order.
    /// <para>
    /// This is the one to use for anything slow. `kind create cluster` pulls a node image and takes
    /// minutes; with buffered output that is indistinguishable from a hang, and the only honest
    /// progress bar is the tool's own words.
    /// </para>
    /// <para>
    /// A clean run ends by completing the sequence; a non-zero exit throws
    /// <see cref="ToolFailedException"/> at the end of enumeration. The exit code is not smuggled in
    /// as a last line, because a caller that only renders the lines would then show failure as
    /// success.
    /// </para>
    /// </summary>
    /// <exception cref="ToolNotFoundException">The tool is not installed.</exception>
    /// <exception cref="ToolFailedException">The tool ran and exited non-zero.</exception>
    IAsyncEnumerable<ToolLine> StreamAsync(ToolInvocation invocation, CancellationToken ct = default);
}
