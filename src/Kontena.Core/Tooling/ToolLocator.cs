namespace Kontena.Core.Tooling;

/// <summary>
/// Where to look for a tool. PATH first, then the places package managers actually install to.
/// </summary>
/// <remarks>
/// Searching beyond PATH is not belt-and-braces, it is the main case on macOS. A desktop app launched
/// from Finder or the Dock inherits a minimal environment — no shell profile is read — so
/// <c>/opt/homebrew/bin</c> and <c>/usr/local/bin</c> are missing from PATH even though every tool the
/// user installed lives there. The same app started from a terminal finds everything. "Not installed"
/// must not depend on how Kontena was started.
/// </remarks>
public static class ToolLocator
{
    /// <summary>
    /// Directories searched after PATH, per platform. Ordered by how likely they are to hold a
    /// current install rather than a stale one.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return
            [
                Path.Combine(home, "scoop", "shims"),
                Path.Combine(localAppData, "Microsoft", "WindowsApps"),
                Path.Combine(programFiles, "Kubernetes", "Minikube"),
                @"C:\ProgramData\chocolatey\bin",
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/opt/homebrew/bin",      // Apple Silicon Homebrew
                "/usr/local/bin",         // Intel Homebrew, and where most installers drop things
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, "bin"),
                "/opt/local/bin",         // MacPorts
            ];
        }

        return
        [
            "/usr/local/bin",
            "/usr/bin",
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "bin"),
            "/var/lib/flatpak/exports/bin",
            "/snap/bin",
            "/home/linuxbrew/.linuxbrew/bin",
        ];
    }

    /// <summary>
    /// The absolute path of an executable, or null when it is nowhere to be found. PATH wins over the
    /// defaults: if the user arranged for a particular one to be first, that is the one they mean.
    /// </summary>
    public static string? Locate(string executable, IReadOnlyList<string>? extraPaths = null)
    {
        // An absolute path is not a search — it is an answer, or it is wrong. Callers that already
        // resolved a tool once pass it back in this form rather than resolving it twice.
        if (Path.IsPathRooted(executable))
            return File.Exists(executable) ? executable : null;

        foreach (var directory in SearchOrder(extraPaths))
        {
            foreach (var name in CandidateNames(executable))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchOrder(IReadOnlyList<string>? extraPaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (seen.Add(directory))
                yield return directory;

        foreach (var directory in extraPaths ?? [])
            if (seen.Add(directory))
                yield return directory;

        foreach (var directory in DefaultSearchPaths())
            if (seen.Add(directory))
                yield return directory;
    }

    /// <summary>
    /// On Windows an executable is only executable with the right extension, and package managers use
    /// all three — scoop writes .cmd shims, winget installs .exe.
    /// </summary>
    private static IEnumerable<string> CandidateNames(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return executable;
            yield break;
        }

        if (Path.HasExtension(executable))
        {
            yield return executable;
            yield break;
        }

        yield return executable + ".exe";
        yield return executable + ".cmd";
        yield return executable + ".bat";
        yield return executable;
    }
}
