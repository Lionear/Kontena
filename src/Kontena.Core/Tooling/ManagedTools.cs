namespace Kontena.Core.Tooling;

/// <summary>
/// Makes a tool runnable regardless of whether the user installed it or Kontena fetched it.
/// <para>
/// Needed because the two halves of the seam disagree on purpose. <see cref="ManagedToolStore"/> keeps
/// Kontena's own copies out of PATH — deliberately, so nothing on the machine picks them up by
/// accident — while <see cref="ToolRunner"/> resolves a tool through PATH. Detecting a downloaded kind
/// and then failing to run it is the gap between those two, and it lands exactly on the person who
/// took Kontena up on the offer to install it.
/// </para>
/// </summary>
public static class ManagedTools
{
    /// <summary>
    /// The tool as it should be run right now: unchanged when a system install exists, or pointed at
    /// Kontena's verified copy when it does not.
    /// <para>
    /// A system install still wins, because <see cref="ToolLocator"/> searches PATH before the extra
    /// paths — the same precedence <see cref="ToolReadinessCheck"/> applies, so what runs is what the
    /// settings page said is being used.
    /// </para>
    /// <para>
    /// The managed copy's checksum is re-verified here, on every resolve, which is the whole point of
    /// keeping it out of PATH: a file Kontena hands to a process is one it just checked.
    /// </para>
    /// </summary>
    public static async ValueTask<ExternalTool> ResolveAsync(
        ExternalTool tool,
        IToolRunner runner,
        ManagedToolStore? store = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(runner);

        if ((await runner.FindAsync(tool, ct)).Found)
            return tool;

        var managed = await (store ?? new ManagedToolStore()).VerifiedPathAsync(tool, ct);
        if (managed is null || Path.GetDirectoryName(managed) is not { Length: > 0 } directory)
            return tool;

        return tool with { ExtraSearchPaths = [.. tool.ExtraSearchPaths, directory] };
    }
}
