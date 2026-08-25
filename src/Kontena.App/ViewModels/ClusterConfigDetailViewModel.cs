using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
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
    public string KeyCountText => IsEditing || _appliedKeyCount is not null
        ? Keys.Count == 1 ? "1 key" : $"{Keys.Count} keys"
        : _row.KeyCount;

    /// <summary>
    /// Set once an apply has landed, so the header stops quoting the listing this page was opened
    /// from: that row was read before the write and still says "2 keys" over three.
    /// </summary>
    private int? _appliedKeyCount;

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
    // Check and Apply go through the object's own manifest and the engine's one write door,
    // ApplyAsync (KON-422). They were a preview that set a status and sent nothing, which read as a
    // save and was not one.

    /// <summary>Whether the object can be written at all — false hides the button entirely.</summary>
    public bool CanEdit => _cluster.Capabilities.Apply && !IsExternallyManaged;

    /// <summary>
    /// Whether a controller owns this object's contents (KON-422).
    /// <para>
    /// Today that is the External Secrets Operator, which reconciles the Secret from an
    /// ExternalSecret. Editing one is not blocked by the cluster — the write goes through, and then
    /// gets undone at the next reconcile, which is a worse outcome than not offering the button:
    /// the value looks changed until something quietly changes it back.
    /// </para>
    /// </summary>
    public bool IsExternallyManaged => _row.IsExternallyManaged;

    /// <summary>
    /// Why Edit is not there, said in one line and in a normal voice.
    /// <para>
    /// Not an error, not a warning: nothing is wrong with this Secret. It is a fact about where its
    /// values come from, so it is drawn as the muted note it is rather than borrowing the styling
    /// that means something needs attention.
    /// </para>
    /// </summary>
    public string ExternallyManagedNotice =>
        "Managed by the External Secrets Operator — its values come from the ExternalSecret that "
        + "reconciles it, so a change made here would be overwritten. Edit the ExternalSecret instead.";

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

    public bool CanApply => IsEditing && IsDirty && !IsBusy;

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
    private Task CheckAsync() => SendAsync(dryRun: true);

    [RelayCommand]
    private Task ApplyAsync() => SendAsync(dryRun: false);

    [ObservableProperty] private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    /// <summary>
    /// Write the fields back through the object's own manifest (KON-422).
    /// <para>
    /// Through the manifest and not through a second engine call: <see cref="IClusterEngine"/> has
    /// one door for writing, <see cref="IClusterEngine.ApplyAsync"/>, and everything an apply gets
    /// right — server-side dry-run, the apiserver's own message when it says no, the diff — comes
    /// free by using it. What this adds is only the encoding, which is the part a field editor
    /// exists to do for you.
    /// </para>
    /// <para>
    /// The manifest is fetched here rather than held: between opening the page and pressing Apply, a
    /// controller may have written to the object, and rewriting a copy from minutes ago would revert
    /// whatever it did. Only the data block is replaced; everything else travels back as it came.
    /// </para>
    /// </summary>
    private async Task SendAsync(bool dryRun)
    {
        if (!CanApply)
            return;

        if (FirstFault() is { } fault)
        {
            StatusIsError = true;
            Status = fault;
            return;
        }

        IsBusy = true;
        Status = null;
        StatusIsError = false;

        try
        {
            Services.Diag.Action(dryRun ? "preview config fields" : "apply config fields", "config detail");

            var manifest = await _cluster.GetManifestAsync(Reference);
            var yaml = ConfigManifest.WithData(manifest, ConfigManifest.DataOf(Keys.Select(k => k.ToEntry())));

            if (yaml is null)
            {
                // Refused rather than guessed at. Naming the YAML tab matters: it is the same
                // object, and it can edit what this cannot.
                StatusIsError = true;
                Status = "This object's manifest is not one this editor can rewrite. Use the YAML tab.";
                return;
            }

            var results = new List<ApplyProgress>();
            await foreach (var step in _cluster.ApplyAsync(
                new ManifestBundle { Yaml = yaml, Source = "config detail", DryRun = dryRun }))
            {
                results.Add(step);
            }

            if (results.Find(r => r.Action == ApplyAction.Failed) is { } failed)
            {
                // The apiserver's own message. It names the field it rejected, and summarising that
                // into "apply failed" sends someone to a terminal to find out which.
                StatusIsError = true;
                Status = failed.Error ?? "The cluster refused it.";
                return;
            }

            if (results.TrueForAll(r => r.Action == ApplyAction.Unchanged))
            {
                Status = dryRun
                    ? "No change — these values already match what the cluster holds."
                    : "No change — the values already matched.";
                return;
            }

            var what = string.Join(", ", results.Select(
                r => $"{r.Resource.Kind.Kind}/{r.Resource.Name} {r.Action.ToString().ToLowerInvariant()}"));

            if (dryRun)
            {
                // Future tense, because nothing has happened. A dry-run that reports "configured"
                // reads as done, and then Apply looks redundant.
                Status = $"Would change · {what}";
                return;
            }

            // Re-read rather than assume: defaulting and admission webhooks get a say, so what the
            // cluster now holds is not simply what was typed. This is also what makes the edit
            // survive — the fields become a reading of the written object, and leaving the tab has
            // nothing left to undo.
            await ReloadAsync();
            Status = $"Applied · {what}";
        }
        catch (Exception failure)
        {
            StatusIsError = true;
            Status = failure.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// What the cluster would reject, said here instead — a round trip to be told a key has no name
    /// is a round trip nobody needed.
    /// </summary>
    private string? FirstFault()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Name))
                return "A key needs a name.";

            if (!seen.Add(key.Name))
                return $"There are two keys called {key.Name}. A key can only be in the object once.";
        }

        return null;
    }

    /// <summary>
    /// Rebuild the fields from what the cluster now holds, and leave editing. The rows are new ones:
    /// a key added in this edit has a size and a stored value now, and a row that was standing in
    /// for it knows neither.
    /// </summary>
    private async Task ReloadAsync()
    {
        var entries = await _cluster.GetConfigDataAsync(Reference);
        var keys = entries.Select(e => new ConfigKey(e.Key, e.SizeBytes)).ToList();

        _appliedKeyCount = keys.Count;
        IsEditing = false;

        Keys.Clear();
        foreach (var row in _row.BuildKeyRows(keys))
            Keys.Add(row);

        _snapshot = [.. Keys];
        Recompute();
    }

    private void Recompute()
    {
        // A status describes one state of the fields, so it cannot outlive it: with this missing,
        // "Would change ·" sat there unchanged while you typed a different value under it, and an
        // "Applied ·" from one edit still read as a confirmation of the next.
        Status = null;
        StatusIsError = false;

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
