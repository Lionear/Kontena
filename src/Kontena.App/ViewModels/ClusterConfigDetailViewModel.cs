using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IClusterEngine _cluster;

    public ClusterConfigDetailViewModel(
        IClusterEngine cluster, ConfigObjectRow row, Action<Pod>? onOpenPod = null,
        Action? onDelete = null)
        : base(cluster, RefOf(row), onOpenPod, onDelete)
    {
        _row = row;
        _cluster = cluster;
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
    /// <summary>
    /// While editing, what is on screen — the listing's count would say "2 keys" over three rows the
    /// moment you add one, and a header that disagrees with the list under it is a header nobody
    /// trusts.
    /// </summary>
    public string KeyCountText => IsEditing
        ? Keys.Count == 1 ? "1 key" : $"{Keys.Count} keys"
        : _row.KeyCount;

    /// <summary>
    /// The keys, each able to fetch its own value on request. Same rows, same discipline as the list
    /// page had (KON-249): nothing is cached, hiding drops the value, and Copy never puts one on
    /// screen. What changed is only where they are shown.
    /// </summary>
    public ObservableCollection<ConfigKeyRow> Keys { get; }

    public bool HasKeys => Keys.Count > 0;

    // ---- Editing the Data tab (KON-418) ----------------------------------------------------------
    //
    // Editing a Secret used to mean the manifest editor as a modal (KON-252), which was right when
    // these two kinds were rows that expanded and had nowhere to put a tab. KON-330 gave them a
    // detail with tabs, and the modal outlived its reason: a Secret's keys are already on this tab,
    // masked, with the reveal and the copy the page was built around. So editing happens here, and
    // "Edit" on the list is now simply the way in to this page.
    //
    // DESIGN PREVIEW: Check and Apply set a status and send nothing. The real ones would go through
    // ManifestEditorViewModel's flow — the manifest this tab is a view of, with the fields written
    // back into it — so there is no second apply path and no new engine API.

    /// <summary>Whether the object can be written at all — false hides the button entirely.</summary>
    public bool CanEdit => _cluster.Capabilities.Apply;

    [ObservableProperty] private bool _isEditing;

    partial void OnIsEditingChanged(bool value) => Recompute();

    /// <summary>The rows as they stood when editing began, so Revert has something to restore.</summary>
    private ConfigKeyRow[] _snapshot = [];

    /// <summary>
    /// Turn the readings into fields (KON-418).
    /// <para>
    /// Every value is fetched here, at once — which is a weaker promise than this page makes while
    /// reading, where a value is fetched per key and dropped again when you hide it. It has to be:
    /// Apply sends the whole object back, so a key nobody looked at still has to be in hand. Making
    /// it a button rather than the state the page opens in is what keeps the reading promise intact
    /// for the times you only came to look.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task BeginEditAsync()
    {
        Status = null;
        StatusIsError = false;
        _snapshot = [.. Keys];

        foreach (var row in Keys)
        {
            Watch(row);
            await row.BeginEditAsync();
        }

        IsEditing = true;
        Recompute();
    }

    /// <summary>Leave editing, dropping every secret value the way hiding one does.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        Restore();

        foreach (var row in Keys)
            row.EndEdit();

        IsEditing = false;
        Status = null;
        StatusIsError = false;
        Recompute();
    }

    /// <summary>Throw away the edits without leaving the fields — the manifest editor's Revert.</summary>
    [RelayCommand]
    private void Revert()
    {
        Restore();

        foreach (var row in Keys)
            row.UndoCommand.Execute(null);

        Status = null;
        StatusIsError = false;
        Recompute();
    }

    private void Restore()
    {
        Keys.Clear();
        foreach (var row in _snapshot)
            Keys.Add(row);
    }

    [RelayCommand]
    private void AddKey()
    {
        var row = ConfigKeyRow.NewKey(IsSecret);
        Watch(row);
        Keys.Add(row);
        Recompute();
    }

    private void Watch(ConfigKeyRow row)
    {
        row.Changed = Recompute;
        row.Removed = r =>
        {
            Keys.Remove(r);
            Recompute();
        };
    }

    /// <summary>Whether anything here differs from what the cluster holds.</summary>
    public bool IsDirty =>
        Keys.Count != _snapshot.Length || Keys.Any(k => k.IsChanged || k.IsNew);

    public bool CanApply => IsEditing && IsDirty;

    /// <summary>The result of the last check or apply, in the cluster's words where there are any.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _statusIsError;

    public IBrush StatusBrush =>
        new SolidColorBrush(Color.Parse(StatusIsError ? "#F87171" : "#34D399"));

    partial void OnStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(StatusBrush));

    /// <summary>
    /// Ask the cluster what this would do, without doing it — the same server-side dry-run the
    /// manifest editor offers, and the answer to the objection that a field editor encodes out of
    /// sight: you can still see exactly what the apiserver would accept.
    /// </summary>
    [RelayCommand]
    private void Check() =>
        Status = $"Would change · {Reference.Kind.Kind}/{Reference.Name} configured";

    [RelayCommand]
    private void Apply() =>
        Status = $"Applied · {Reference.Kind.Kind}/{Reference.Name} configured";

    private void Recompute()
    {
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(KeyCountText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanApply));
    }

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
