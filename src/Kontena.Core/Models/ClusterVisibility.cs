namespace Kontena.Core.Models;

/// <summary>
/// Which clusters belong in the switcher (KON-120).
/// <para>
/// Three states, and the difference between them is the whole feature: <b>chosen</b> (in the switcher),
/// <b>declined</b> (seen, not wanted, and not to be asked about again), and <b>new</b> (never seen, so
/// worth mentioning once). A plain list of wanted clusters cannot tell "declined" from "new", and would
/// re-offer a context every launch after the user said no.
/// </para>
/// </summary>
public static class ClusterVisibility
{
    /// <summary>Whether this cluster should appear in the switcher.</summary>
    public static bool ShowsCluster(this KontenaSettings settings, string backend)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.KnownClusters.TryGetValue(backend, out var shown) && shown;
    }

    /// <summary>Clusters that were discovered but have never been offered to the user.</summary>
    public static IReadOnlyList<string> NewClusters(
        this KontenaSettings settings, IEnumerable<string> discovered)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(discovered);

        return [.. discovered.Where(id => !settings.KnownClusters.ContainsKey(id))];
    }

    /// <summary>Settings with this cluster shown or hidden, recorded either way.</summary>
    public static KontenaSettings WithCluster(this KontenaSettings settings, string backend, bool shown)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var known = new Dictionary<string, bool>(settings.KnownClusters, StringComparer.Ordinal)
        {
            [backend] = shown,
        };

        return settings with { KnownClusters = known };
    }

    /// <summary>
    /// The one-time adoption for an installation that predates this: everything already discovered was
    /// already in the switcher, so it stays there. Without this, updating Kontena would silently empty
    /// the cluster side of someone's switcher — a regression dressed up as a feature.
    /// <para>
    /// Only for an installation that has been used. A fresh one has nothing to preserve, and its user
    /// should be the one choosing.
    /// </para>
    /// <para>
    /// And only for one that predates the question (KON-351). "Onboarded with no answers" used to mean
    /// exactly that, until skipping the wizard began leaving the same state behind — at which point the
    /// next launch answered "yes, all of them" to a question the user had just declined to answer.
    /// <see cref="KontenaSettings.ClusterChoiceOffered"/> is what tells the two apart.
    /// </para>
    /// </summary>
    public static KontenaSettings AdoptExistingClusters(
        this KontenaSettings settings, IEnumerable<string> discovered)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(discovered);

        if (!settings.Onboarded || settings.ClusterChoiceOffered || settings.KnownClusters.Count > 0)
            return settings;

        var adopted = discovered.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => true, StringComparer.Ordinal);

        return adopted.Count == 0 ? settings : settings with { KnownClusters = adopted };
    }

    /// <summary>
    /// Drops clusters that are no longer in any kubeconfig, so the file does not keep an entry for every
    /// context the user has ever had.
    /// </summary>
    public static KontenaSettings PruneClusters(
        this KontenaSettings settings, IEnumerable<string> discovered)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(discovered);

        var alive = new HashSet<string>(discovered, StringComparer.Ordinal);
        if (settings.KnownClusters.Keys.All(alive.Contains))
            return settings;

        return settings with
        {
            KnownClusters = settings.KnownClusters
                .Where(pair => alive.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }
}
