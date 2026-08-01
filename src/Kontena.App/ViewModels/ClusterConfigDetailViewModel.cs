using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// A ConfigMap or a Secret opened in the drawer (KON-330).
/// <para>
/// The two list pages used to answer with an expander: a chevron unfolded the keys in place, and that
/// was everything a config object could be. Every other kind in Kontena opens a detail — and gets the
/// full page and the window of its own that come with it (KON-307, KON-308) — so these two now do too,
/// and the expander is gone rather than kept as a second place a value can appear.
/// </para>
/// <para>
/// One view model for both kinds, like <see cref="ConfigObjectRow"/> itself: the difference between
/// them is whether a value is worth hiding, and that lives on <see cref="ConfigKeyRow"/> already.
/// </para>
/// </summary>
public sealed partial class ClusterConfigDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly ConfigObjectRow _row;

    public ClusterConfigDetailViewModel(
        IClusterEngine cluster, ConfigObjectRow row, Action<Pod>? onOpenPod = null)
        : base(cluster, RefOf(row), onOpenPod)
    {
        _row = row;
        Keys = [.. row.BuildKeyRows()];

        _ = LoadPodsAsync();
    }

    private static ResourceRef RefOf(ConfigObjectRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Reference;
    }

    public bool IsSecret => _row.IsSecret;

    /// <summary>Secrets carry a type, ConfigMaps do not — the header hides the field rather than
    /// showing a dash where there was never a question.</summary>
    public bool HasType => _row.HasType;

    public string TypeText => _row.Type;
    public string AgeText => _row.Age;
    public string KeyCountText => _row.KeyCount;

    /// <summary>
    /// The keys, each able to fetch its own value on request. Same rows, same discipline as the list
    /// page had (KON-249): nothing is cached, hiding drops the value, and Copy never puts one on
    /// screen. What changed is only where they are shown.
    /// </summary>
    public ObservableCollection<ConfigKeyRow> Keys { get; }

    public bool HasKeys => Keys.Count > 0;

    /// <summary>"Used by", not "Pods": these pods are not owned by this object, they read it.</summary>
    public override string PodsTabLabel => "Used by";

    /// <summary>
    /// Which pods read this object, from the references the pod spec already carries (KON-330).
    /// <para>
    /// Not a label-selector match like every other detail page does — nothing labels a pod with the
    /// secrets it mounts. This is the actual question you have on a secret's page: can this go?
    /// </para>
    /// </summary>
    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        var mine = all.Where(p => p.ConfigUses.Any(Matches)).ToList();
        UsedBySummary = Summarise(mine);
        return mine;
    }

    private bool Matches(ConfigUse use) =>
        use.Kind == Reference.Kind && use.Name == Reference.Name;

    protected override string EmptyPodsReason() => IsSecret
        ? "No pod in this namespace mounts this secret, reads it as environment, or pulls images with it."
        : "No pod in this namespace mounts this config map or reads it as environment.";

    /// <summary>
    /// How the pods use it, in one line — the part that decides what deleting it breaks. A mounted
    /// object stops the next pod from starting at all; a single environment key may only surface much
    /// later, deep inside the app.
    /// </summary>
    [ObservableProperty] private string _usedBySummary = string.Empty;

    private string Summarise(IReadOnlyList<Pod> pods)
    {
        if (pods.Count == 0)
            return string.Empty;

        // Counted per pod rather than per reference: a secret mounted into three containers of one pod
        // is one pod that stops starting, not three.
        var parts = new List<string>();

        // Phrased so only the noun has to agree — "mounted by 1 pod", "mounted by 3 pods". A verb that
        // had to agree as well is how "1 pod mount it" gets written.
        foreach (var (how, phrase) in Phrases)
        {
            var n = pods.Count(p => p.ConfigUses.Any(u => Matches(u) && u.How == how));
            if (n > 0)
                parts.Add($"{phrase} {n} {(n == 1 ? "pod" : "pods")}");
        }

        return string.Join(" · ", parts);
    }

    private static readonly (ConfigUseKind How, string Phrase)[] Phrases =
    [
        (ConfigUseKind.Volume, "mounted by"),
        (ConfigUseKind.EnvironmentVariable, "read as environment by"),
        (ConfigUseKind.EnvironmentFrom, "read whole as environment by"),
        (ConfigUseKind.ImagePullSecret, "used to pull images by"),
    ];
}
