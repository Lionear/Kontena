using Kontena.Sdk.Models;

namespace Kontena.Core.Shell;

/// <summary>What a cluster terminal needs to know about the cluster it opens onto.</summary>
/// <param name="Context">Context name as it appears in the kubeconfig.</param>
/// <param name="Cluster">The context's cluster name — a reference, not a credential.</param>
/// <param name="User">The context's user name — likewise.</param>
/// <param name="Namespace">Namespace to start in, or null to leave the context's own.</param>
/// <param name="KubeconfigPaths">Files already in play, in the order they should keep.</param>
public sealed record ClusterShellRequest(
    string Context,
    string? Cluster,
    string? User,
    string? Namespace,
    IReadOnlyList<string> KubeconfigPaths);

/// <summary>
/// Opens a shell on this machine that starts on the cluster Kontena is showing, with <c>k</c> aliased to
/// <c>kubectl</c> (KON-171).
/// <para>
/// Everything it generates lives in one directory per session and goes away with it — the kubeconfig
/// overlay that names the context, and the rcfile that carries the alias. Nothing is written into the
/// user's own configuration, so there is nothing to undo if Kontena is killed rather than closed.
/// </para>
/// </summary>
public static class HostShellLauncher
{
    /// <summary>The overlay's file name inside the session directory.</summary>
    internal const string OverlayFileName = "kubeconfig.yaml";

    /// <summary>
    /// Start a shell for <paramref name="request"/> sized to the given grid.
    /// </summary>
    public static async ValueTask<IExecSession> OpenAsync(
        ClusterShellRequest request,
        int columns,
        int rows,
        CancellationToken ct = default)
    {
        var directory = CreateSessionDirectory();

        try
        {
            var overlayPath = Path.Combine(directory, OverlayFileName);
            WritePrivate(
                overlayPath,
                KubeContextOverlay.Compose(
                    request.Context, request.Cluster, request.User, request.Namespace));

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KUBECONFIG"] = KubeContextOverlay.ComposeKubeconfigValue(
                    overlayPath, request.KubeconfigPaths),

                // So a shell started from Kontena can say so — in a prompt, or in a bug report.
                ["KONTENA_CONTEXT"] = request.Context,
            };

            if (request.Namespace is { Length: > 0 } ns)
                environment["KONTENA_NAMESPACE"] = ns;

            var plan = HostShell.Plan(HostShell.Detect(), directory, environment);

            foreach (var (name, content) in plan.SupportFiles)
                WritePrivate(Path.Combine(directory, name), content);

            return await PtyShellSession
                .StartAsync(plan, HomeDirectory(), columns, rows, directory, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    /// <summary>
    /// A directory of its own per session, readable only by its owner. The overlay names a cluster and
    /// the rcfile names the user's own dotfiles; neither is secret, but neither is anyone else's
    /// business either — the same reasoning that made <c>settings.json</c> owner-only (KON-187).
    /// </summary>
    private static string CreateSessionDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-shell-{Guid.NewGuid():N}");

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Where a shell should open: the user's home, as any terminal would.</summary>
    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(home) ? home : Directory.GetCurrentDirectory();
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
