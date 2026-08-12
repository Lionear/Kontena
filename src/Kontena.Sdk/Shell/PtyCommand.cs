namespace Kontena.Sdk.Shell;

/// <summary>
/// What to start inside a pseudo-terminal: a program, its arguments, and anything to add to the
/// inherited environment.
/// <para>
/// Deliberately smaller than the host shell's own plan, which also carries the support files an rcfile
/// and a kubeconfig overlay need. Those are the app's business; an adapter opening a shell in a
/// container has nothing to write to disk first, and a seam that asked it for support files would be
/// asking every caller to answer a question only one of them has.
/// </para>
/// </summary>
/// <param name="Executable">Absolute path (or bare name) of the program to run.</param>
/// <param name="Arguments">Arguments in order, unquoted — the PTY layer passes them as an argv.</param>
/// <param name="Environment">Variables to add to (or override in) the inherited environment.</param>
public sealed record PtyCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>A command that adds nothing to the environment — the common case for a container exec.</summary>
    public PtyCommand(string executable, IReadOnlyList<string> arguments)
        : this(executable, arguments, new Dictionary<string, string>(StringComparer.Ordinal)) { }
}
