namespace Kontena.Core.Shell;

/// <summary>
/// The one-file kubeconfig overlay that points a shell at the cluster Kontena is showing, without
/// touching the kubeconfig the user owns (KON-171).
/// <para>
/// <c>KUBECONFIG</c> takes a list of files, and for a single-valued key like <c>current-context</c> the
/// first file that sets it wins. So Kontena writes a tiny file that sets nothing else, puts it in front
/// of the paths that were already there, and the shell starts on the right cluster. Nothing is copied
/// and nothing of the user's is rewritten; closing the terminal removes the file and the effect.
/// </para>
/// <para>
/// Deliberately <em>not</em> <c>kubectl config use-context</c>: that writes to the shared file and moves
/// the context in every other shell the user has open. A convenience button that silently changes
/// someone's whole environment is the kind of surprise found later, while debugging something else.
/// </para>
/// <para>
/// Deliberately not <c>kubectl config view --flatten --minify</c> either — the recipe found everywhere —
/// because flattening writes certificates and tokens out to a temporary file. For a convenience button
/// that is the wrong trade.
/// </para>
/// </summary>
public static class KubeContextOverlay
{
    /// <summary>
    /// The overlay's contents for <paramref name="context"/>.
    /// <para>
    /// The namespace is only pinned when the context's cluster and user are both known, because pinning
    /// it means writing a <c>contexts</c> entry, and for list-valued keys the first file wins per name —
    /// an entry naming no cluster and no user would <em>shadow</em> the real one and leave the shell
    /// pointing nowhere. Cluster and user here are the names as they appear in the user's own file, not
    /// credentials; nothing secret is copied.
    /// </para>
    /// </summary>
    public static string Compose(string context, string? cluster, string? user, string? @namespace)
    {
        var lines = new List<string>
        {
            "# Written by Kontena for this terminal session only. Safe to delete.",
            "apiVersion: v1",
            "kind: Config",
            $"current-context: {Quote(context)}",
        };

        if (!string.IsNullOrWhiteSpace(@namespace)
            && !string.IsNullOrWhiteSpace(cluster)
            && !string.IsNullOrWhiteSpace(user))
        {
            lines.AddRange(
            [
                "contexts:",
                $"  - name: {Quote(context)}",
                "    context:",
                $"      cluster: {Quote(cluster)}",
                $"      user: {Quote(user)}",
                $"      namespace: {Quote(@namespace)}",
            ]);
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The <c>KUBECONFIG</c> value: the overlay first, then the files that were already in play, with
    /// duplicates and blanks dropped so the list says each file once.
    /// </summary>
    /// <param name="existing">
    /// The paths already in effect — the user's <c>KUBECONFIG</c> entries, the default file, and any
    /// kubeconfig added to Kontena. Order is kept; only the overlay jumps the queue.
    /// </param>
    public static string ComposeKubeconfigValue(string overlayPath, IEnumerable<string> existing)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();

        foreach (var path in new[] { overlayPath }.Concat(existing))
        {
            var trimmed = path?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                paths.Add(trimmed);
        }

        return string.Join(Path.PathSeparator, paths);
    }

    /// <summary>
    /// Double-quoted, because context names are not always plain words — an EKS context is
    /// <c>arn:aws:eks:eu-west-1:…</c>, and a bare colon starts a mapping in YAML.
    /// </summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
