using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// An in-memory <see cref="IHelmReleases"/> (KON-473): two releases, one of them with a failed upgrade
/// on top of a good revision, so the list, the history and a rollback all have something to show.
/// <para>
/// Live like <see cref="FakeAlertSource"/>: an upgrade adds a revision, a rollback adds one that says
/// so, and an uninstall removes the release — the page can be driven end to end without a cluster.
/// </para>
/// </summary>
public sealed class FakeHelmReleases : IHelmReleases
{
    private sealed class Entry
    {
        public required string Name { get; init; }
        public required string Namespace { get; init; }
        public required string Chart { get; init; }
        public List<(HelmRevision Revision, string Values)> History { get; } = [];
    }

    private readonly List<Entry> _releases;

    /// <summary>Every write that came through, in order — "uninstall apps/web", "rollback apps/web 1", ….</summary>
    public List<string> Writes { get; } = [];

    public FakeHelmReleases()
    {
        var now = DateTimeOffset.UtcNow;

        var web = new Entry { Name = "web", Namespace = "default", Chart = "ingress-nginx" };
        web.History.Add((Revision(1, "superseded", "ingress-nginx-4.10.0", "Install complete", now.AddDays(-9)), "controller:\n  replicaCount: 1\n"));
        web.History.Add((Revision(2, "failed", "ingress-nginx-4.10.1", "Upgrade \"web\" failed: timed out waiting for the condition", now.AddHours(-3)), "controller:\n  replicaCount: 3\n"));

        var cache = new Entry { Name = "cache", Namespace = "kube-system", Chart = "redis" };
        cache.History.Add((Revision(1, "deployed", "redis-19.0.2", "Install complete", now.AddDays(-30)), string.Empty));

        _releases = [web, cache];
    }

    public ValueTask<IReadOnlyList<HelmRelease>> ListAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HelmRelease>>(
        [
            .. _releases.Where(r => ns is null || r.Namespace == ns).Select(r =>
            {
                var current = r.History[^1].Revision;
                return new HelmRelease
                {
                    Name = r.Name,
                    Namespace = r.Namespace,
                    Chart = r.Chart,
                    ChartVersion = current.Chart[(r.Chart.Length + 1)..],
                    AppVersion = current.AppVersion,
                    Status = current.Status,
                    Revision = current.Revision,
                    Updated = current.Updated,
                };
            }),
        ]);

    public ValueTask<string> GetValuesAsync(string release, string ns, bool all = false, CancellationToken ct = default) =>
        ValueTask.FromResult(all
            ? "image:\n  tag: latest\n" + Find(release, ns).History[^1].Values
            : Find(release, ns).History[^1].Values);

    public ValueTask<string> GetManifestAsync(string release, string ns, CancellationToken ct = default) =>
        ValueTask.FromResult($"---\n# Source: {Find(release, ns).Chart}/templates/deployment.yaml\napiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: {release}\n");

    public ValueTask<IReadOnlyList<HelmRevision>> GetHistoryAsync(string release, string ns, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HelmRevision>>([.. Find(release, ns).History.Select(h => h.Revision)]);

    public ValueTask<string?> UpgradeAsync(HelmUpgrade upgrade, CancellationToken ct = default)
    {
        Writes.Add($"upgrade {upgrade.Namespace}/{upgrade.Release} {upgrade.Chart} {upgrade.Version}".TrimEnd());
        var entry = Find(upgrade.Release, upgrade.Namespace);
        var version = upgrade.Version.Length > 0 ? upgrade.Version : entry.History[^1].Revision.Chart[(entry.Chart.Length + 1)..];
        Supersede(entry);
        entry.History.Add((Revision(entry.History.Count + 1, "deployed", $"{entry.Chart}-{version}", "Upgrade complete", DateTimeOffset.UtcNow), upgrade.ValuesYaml));
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<string?> RollbackAsync(string release, string ns, int revision, CancellationToken ct = default)
    {
        Writes.Add($"rollback {ns}/{release} {revision}");
        var entry = Find(release, ns);
        var target = entry.History.Single(h => h.Revision.Revision == revision);
        Supersede(entry);
        entry.History.Add((target.Revision with
        {
            Revision = entry.History.Count + 1, Status = "deployed", Description = $"Rollback to {revision}", Updated = DateTimeOffset.UtcNow,
        }, target.Values));
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<string?> UninstallAsync(string release, string ns, CancellationToken ct = default)
    {
        Writes.Add($"uninstall {ns}/{release}");
        _releases.Remove(Find(release, ns));
        return ValueTask.FromResult<string?>(null);
    }

    private Entry Find(string release, string ns) =>
        _releases.SingleOrDefault(r => r.Name == release && r.Namespace == ns)
        ?? throw new InvalidOperationException($"Error: release: not found");

    private static void Supersede(Entry entry)
    {
        var (last, values) = entry.History[^1];
        if (last.Status == "deployed")
            entry.History[^1] = (last with { Status = "superseded" }, values);
    }

    private static HelmRevision Revision(int n, string status, string chart, string description, DateTimeOffset updated) =>
        new() { Revision = n, Status = status, Chart = chart, AppVersion = "1.0", Description = description, Updated = updated };
}
