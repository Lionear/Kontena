namespace Kontena.Sdk.Tooling;

/// <summary>
/// Is an engine on this machine at all? The question behind
/// <see cref="IBackendProvider.IsInstalled"/> (KON-255), asked without starting anything.
/// <para>
/// "Installed", never "running". A stopped engine has no live socket and still counts: a row reading
/// "Docker · Not connected" is what someone opened the switcher to find out. Only "there is no trace
/// of this here" is worth hiding.
/// </para>
/// <para>
/// It lives in the SDK rather than in an adapter because every backend that can be absent asks the
/// same question — the two built-in engines today, and whatever a plugin or the store contributes
/// later.
/// </para>
/// </summary>
public static class EnginePresence
{
    /// <summary>
    /// Checks, cheapest first: an environment variable pointing the engine somewhere else, the socket
    /// it opens locally, and its CLI on PATH. Any one is enough — they are three symptoms of the same
    /// thing, and an install with a socket but no CLI (or the other way round, mid-update or with the
    /// daemon in a VM) is as installed as one with both.
    /// </summary>
    /// <param name="environmentVariable">
    /// e.g. <c>DOCKER_HOST</c>. Checked first: a user who set it has already said where their engine
    /// is, and looking for a local socket after that would be answering a question they answered.
    /// </param>
    /// <param name="socketPath">The unix socket. Ignored on Windows.</param>
    /// <param name="windowsPipe">
    /// The named pipe's short name (<c>docker_engine</c>), ignored elsewhere. Found by listing
    /// <c>\\.\pipe\</c> rather than with <see cref="File.Exists"/>, which answers false for a pipe that
    /// is plainly there.
    /// </param>
    /// <param name="executable">The CLI to look for on PATH, via <see cref="ToolLocator"/>.</param>
    public static bool Any(
        string environmentVariable, string socketPath, string windowsPipe, string executable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 })
            return true;

        if (SocketExists(socketPath, windowsPipe))
            return true;

        return ToolLocator.Locate(executable) is not null;
    }

    private static bool SocketExists(string socketPath, string windowsPipe)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return File.Exists(socketPath);

            return Directory.GetFiles(@"\\.\pipe\")
                .Any(p => Path.GetFileName(p).Equals(windowsPipe, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // A path that cannot be looked at says nothing about whether the engine is installed, and
            // the caller has two other signals. Never worth a throw: this runs while building a list
            // the user is waiting on.
            return false;
        }
    }
}
