namespace Kontena.Core.Shell;

/// <summary>
/// Everything needed to start one host shell: what to run, with which arguments and environment, and
/// which support files have to exist first.
/// <para>
/// Kept as data rather than a launch call so the interesting half — which shell, which alias
/// mechanism, which environment — is decided by pure code and can be asserted without spawning
/// anything. <see cref="HostShellLauncher"/> writes <see cref="SupportFiles"/> and starts the process.
/// </para>
/// </summary>
/// <param name="Executable">Absolute path (or bare name) of the shell binary.</param>
/// <param name="Arguments">Arguments in order, unquoted — the PTY layer passes them as an argv.</param>
/// <param name="Environment">Variables to add to (or override in) the inherited environment.</param>
/// <param name="SupportFiles">File name → content, written into the session's support directory.</param>
public sealed record ShellPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> SupportFiles);
