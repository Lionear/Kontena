using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Services;

/// <summary>
/// Points ssh at Kontena's own executable to answer a password prompt (KON-259).
/// <para>
/// The two things it takes to build one live at opposite ends: only the app knows where Kontena is
/// running from, and only the app knows what its keychain entries are called. So it is assembled
/// here and handed down, rather than an adapter guessing at either.
/// </para>
/// </summary>
public static class SshPasswordPrompt
{
    /// <summary>
    /// The helper for this engine, or null when it does not need one — anything but SSH-with-password,
    /// and any situation where <see cref="Environment.ProcessPath"/> is unknown.
    /// </summary>
    /// <remarks>
    /// Returning null is not a fallback to something weaker: with no helper, <c>BatchMode=yes</c> stays
    /// on and ssh fails with its own message instead of waiting for an answer nobody can give.
    /// </remarks>
    public static SshAskpass? For(RemoteEngine remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (remote.Transport != RemoteEngineTransport.Ssh || !remote.UsePassword)
            return null;

        // Null under single-file publishing on some platforms, and empty in a few hosting scenarios.
        // Either way there is no path to hand ssh.
        return Environment.ProcessPath is { Length: > 0 } executable
            ? new SshAskpass(executable, SecretKeys.Engine(remote.Id))
            : null;
    }
}
