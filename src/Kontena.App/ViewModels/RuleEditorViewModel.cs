using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The rule editor (KON-210): one <see cref="AuthoredRule"/>, composed into the
/// <c>PrometheusRule</c> that gets applied and — from KON-211 — written.
/// <para>
/// <b>There is no second apply path.</b> The composer produces YAML, the YAML becomes a
/// <see cref="ManifestBundle"/>, and that goes to the page that already does server-side dry-run,
/// diff and apply. A rule authored here reaches the cluster the same way a pasted manifest does,
/// through the same review step, which is the only reason the review step is worth anything.
/// </para>
/// <para>
/// The manifest panel is not decoration either — same rule as the local-cluster command preview: you
/// see what runs before it runs, and the bytes shown are the bytes applied.
/// </para>
/// </summary>
public partial class RuleEditorViewModel : ViewModelBase, IDisposable
{
    private readonly IClusterEngine _cluster;
    private readonly Action<ManifestBundle>? _onApply;

    private RuleTargeting _targeting = RuleTargeting.Unread(
        "Kontena has not read this cluster's Prometheus yet.");

    private IReadOnlyList<KubeNamespace> _namespaces = [];
    private string? _namespaceListRefusal;

    private readonly Func<Task<RuleTargeting>> _readTargeting;

    /// <param name="onApply">
    /// Hands the composed bundle to the apply page. Passed in rather than reached for, because the
    /// editor's job ends at the manifest — where it goes is the shell's routing decision.
    /// </param>
    /// <param name="readTargeting">
    /// Where the Prometheus' selectors come from. Defaults to the Kubernetes engine's read; a backend
    /// that has none leaves it unread, which the page says rather than papers over.
    /// </param>
    public RuleEditorViewModel(
        IClusterEngine cluster, Action<ManifestBundle>? onApply = null,
        Func<Task<RuleTargeting>>? readTargeting = null)
    {
        _cluster = cluster;
        _onApply = onApply;
        _readTargeting = readTargeting
            ?? (cluster is KubernetesClusterEngine k
                ? () => k.ReadRuleTargetingAsync()
                : () => Task.FromResult(RuleTargeting.Unread(
                    "This backend has no Prometheus custom resource to read, so Kontena cannot say "
                    + "which namespaces and labels rules are selected by.")));
        Check = new PromqlCheckViewModel(cluster is IAlertingAware aware ? aware.Alerts : NoAlertSource.Instance);

        foreach (var name in new[] { "info", "warning", "critical" })
            Severities.Add(new SeverityOption(name, on: name == "warning", Pick: PickSeverity));

        Loaded = LoadAsync();
    }

    /// <summary>The cluster read, so a test can await it instead of sleeping and hoping.</summary>
    internal Task Loaded { get; }

    // ── The rule ─────────────────────────────────────────────────────────────

    [ObservableProperty] private string _alertName = string.Empty;
    [ObservableProperty] private string _expression = string.Empty;
    [ObservableProperty] private string _forText = "10m";
    [ObservableProperty] private string _objectName = string.Empty;

    /// <summary>Free text on purpose — see <see cref="NamespaceVerdict"/>.</summary>
    [ObservableProperty] private string _namespaceName = string.Empty;

    /// <summary>Labels on the alert. <c>severity</c> is prepended at compose time, not held here.</summary>
    public ObservableCollection<RuleLabelRow> Labels { get; } = [];

    public ObservableCollection<RuleLabelRow> Annotations { get; } = [];

    /// <summary>The PromQL check block (KON-209), fed by this page's expression field.</summary>
    public PromqlCheckViewModel Check { get; }

    partial void OnExpressionChanged(string value)
    {
        Check.Expression = value;
        Refresh();
    }

    partial void OnAlertNameChanged(string value) => Refresh();
    partial void OnForTextChanged(string value) => Refresh();
    partial void OnObjectNameChanged(string value) => Refresh();

    partial void OnNamespaceNameChanged(string value)
    {
        Refresh();
        OnPropertyChanged(nameof(NamespaceVerdict));
        OnPropertyChanged(nameof(NamespaceVerdictIsWarning));
        OnPropertyChanged(nameof(NamespaceVerdictBrushKey));
    }

    [RelayCommand]
    private void AddLabel() => Add(Labels);

    [RelayCommand]
    private void AddAnnotation() => Add(Annotations);

    private void Add(ObservableCollection<RuleLabelRow> rows) =>
        rows.Add(new RuleLabelRow(string.Empty, string.Empty, Refresh, row =>
        {
            rows.Remove(row);
            Refresh();
        }));

    // ── Severity is a label, not a schema ────────────────────────────────────

    /// <summary>
    /// Three buttons and no enum. <c>critical</c> means whatever the routing config says it means, so
    /// the editor writes the label and stops there — a dropdown of validated values would be Kontena
    /// inventing a schema Prometheus does not have.
    /// </summary>
    public ObservableCollection<SeverityOption> Severities { get; } = [];

    public string Severity => Severities.FirstOrDefault(s => s.IsOn)?.Name ?? "warning";

    private void PickSeverity(SeverityOption picked)
    {
        foreach (var option in Severities)
            option.IsOn = ReferenceEquals(option, picked);

        OnPropertyChanged(nameof(Severity));
        Refresh();
    }

    // ── The selector label, which is not the author's to remove ──────────────

    /// <summary>
    /// <c>metadata.labels</c> prefilled from the Prometheus' <c>ruleSelector</c>. Distinct from the
    /// alert's labels and far easier to lose: without it the object applies cleanly, Prometheus
    /// ignores it, and nothing anywhere says why.
    /// </summary>
    public ObservableCollection<RuleLabelRow> ObjectLabels { get; } = [];

    public bool HasObjectLabels => ObjectLabels.Count > 0;

    /// <summary>What the field says about the selector — always something, including "could not read it".</summary>
    public string SelectorNotice => _targeting switch
    {
        { SelectorRefusal: { } why } => why,
        { SelectsNothing: true } =>
            "This Prometheus has a null ruleSelector, which selects no PrometheusRule at all — no label "
            + "on this object will change that. It is the Prometheus that needs editing, not the rule.",
        { RequiredLabels.Count: 0 } =>
            "This Prometheus' ruleSelector is empty, so it picks up every PrometheusRule in a watched "
            + "namespace. Nothing extra is needed on this object.",
        _ when ObjectLabels.Any(l => l.Value.Length == 0) =>
            "That label is empty, so this Prometheus will not select the object. It is prefilled from "
            + "ruleSelector and is not yours to drop — put the value back.",
        _ =>
            "Prefilled from this Prometheus' ruleSelector. It lands on the object rather than on the "
            + "alert, and without it the rule applies cleanly and is then ignored — the most common way "
            + "a hand-written PrometheusRule silently does nothing.",
    };

    /// <summary>Colour is the second signal; <see cref="SelectorNotice"/> says it in words first.</summary>
    public string SelectorNoticeBrushKey => SelectorNoticeIsWarning ? "Warn" : "TextFaint";

    /// <summary>Amber whenever the object would not be selected as it stands.</summary>
    public bool SelectorNoticeIsWarning =>
        !_targeting.KnowsSelector || _targeting.SelectsNothing
        || ObjectLabels.Any(l => l.Value.Length == 0)
        || _targeting.RequiredLabels.Count > 0;

    // ── The namespace typeahead ──────────────────────────────────────────────

    /// <summary>Every namespace the cluster reported, each carrying whether rules there are looked at.</summary>
    public ObservableCollection<NamespaceOption> NamespaceOptions { get; } = [];

    /// <summary>What the open menu shows — everything until the first keystroke.</summary>
    public ObservableCollection<NamespaceOption> NamespaceMatches { get; } = [];

    [ObservableProperty] private bool _isNamespaceMenuOpen;

    /// <summary>
    /// Until the user types, the field's value is a <b>selection and not a query</b>: focusing shows
    /// the whole list with the current namespace in it. Without this, opening a filled-in field looks
    /// like a dropdown with exactly one option.
    /// </summary>
    private bool _typed;

    public bool HasNamespaceMatches => NamespaceMatches.Count > 0;

    [RelayCommand]
    private void OpenNamespaceMenu()
    {
        _typed = false;
        FilterNamespaces();
        IsNamespaceMenuOpen = NamespaceOptions.Count > 0;
    }

    [RelayCommand]
    private void CloseNamespaceMenu() => IsNamespaceMenuOpen = false;

    [RelayCommand]
    private void PickNamespace(NamespaceOption? option)
    {
        if (option is null)
            return;

        NamespaceName = option.Name;
        IsNamespaceMenuOpen = false;
    }

    /// <summary>Called by the view on each keystroke: filtering starts at the first one, not before.</summary>
    public void NamespaceTyped()
    {
        _typed = true;
        FilterNamespaces();
        IsNamespaceMenuOpen = NamespaceOptions.Count > 0;
    }

    private void FilterNamespaces()
    {
        var term = _typed ? NamespaceName.Trim() : string.Empty;

        NamespaceMatches.Clear();
        foreach (var option in NamespaceOptions.Where(o =>
                     term.Length == 0 || o.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            NamespaceMatches.Add(option);
        }

        OnPropertyChanged(nameof(HasNamespaceMatches));
    }

    /// <summary>
    /// The sentence under the field, which is the whole point of the list — a namespace that applies
    /// cleanly and is then never looked at is a quieter failure than one the apply refuses, and so the
    /// more dangerous of the two.
    /// </summary>
    public string NamespaceVerdict
    {
        get
        {
            var name = NamespaceName.Trim();

            if (name.Length == 0)
                return "A PrometheusRule needs a namespace.";

            if (_namespaceListRefusal is { } refusal)
                return refusal;

            if (NamespaceOptions.FirstOrDefault(o => o.Name == name) is not { } known)
            {
                return $"{name} does not exist on this cluster. The apply would fail unless you create "
                    + "it first; writing the rule to a file works either way.";
            }

            return known.Watched switch
            {
                true => $"Prometheus watches {name} for rules, so this rule will be picked up.",
                false => $"Prometheus does not watch {name} — the object would apply cleanly and then be "
                    + "ignored. Pick a watched namespace, or widen ruleNamespaceSelector.",
                null => _targeting.NamespaceRefusal ?? "Kontena cannot tell whether this namespace is watched.",
            };
        }
    }

    /// <inheritdoc cref="SelectorNoticeBrushKey"/>
    public string NamespaceVerdictBrushKey => NamespaceVerdictIsWarning ? "Warn" : "TextDim";

    public bool NamespaceVerdictIsWarning
    {
        get
        {
            var name = NamespaceName.Trim();
            if (name.Length == 0 || _namespaceListRefusal is not null)
                return true;

            return NamespaceOptions.FirstOrDefault(o => o.Name == name)?.Watched is not true;
        }
    }

    // ── The manifest, and where it goes ──────────────────────────────────────

    /// <summary>
    /// The rule as authored. Severity goes in first so the composed labels read the way the form does.
    /// </summary>
    public AuthoredRule Rule => new()
    {
        Name = AlertName.Trim(),
        Expr = Expression,
        For = PromDuration.TryParse(ForText, out var wait) ? wait : null,
        Labels = Pairs(Severity, Labels),
        Annotations = Pairs(null, Annotations),
        ObjectName = ObjectName.Trim(),
        Namespace = NamespaceName.Trim(),
        ObjectLabels = Pairs(null, ObjectLabels),
    };

    private static Dictionary<string, string> Pairs(
        string? severity, IEnumerable<RuleLabelRow> rows)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (severity is { Length: > 0 })
            pairs["severity"] = severity;

        foreach (var row in rows.Where(r => r.Key.Trim().Length > 0))
            pairs[row.Key.Trim()] = row.Value;

        return pairs;
    }

    /// <summary>Exactly what gets applied and, from KON-211, written. Nothing injected either way.</summary>
    public string Manifest => PrometheusRuleComposer.Compose(Rule);

    /// <summary>What is still missing, in the order the form asks for it; empty when nothing is.</summary>
    public string? Incomplete
    {
        get
        {
            if (AlertName.Trim().Length == 0)
                return "The alert needs a name — it becomes the alertname label.";
            if (Expression.Trim().Length == 0)
                return "The rule needs an expression.";
            if (ForText.Trim().Length > 0 && !PromDuration.TryParse(ForText, out _))
                return $"\"{ForText.Trim()}\" is not a Prometheus duration. Try 30s, 10m or 1h30m.";
            if (ObjectName.Trim().Length == 0)
                return "The PrometheusRule object needs a name.";
            if (NamespaceName.Trim().Length == 0)
                return "The PrometheusRule object needs a namespace.";

            return null;
        }
    }

    public bool IsComplete => Incomplete is null;

    /// <summary>
    /// Whether the cluster could take the rule at all. Independent of everything else on the page: a
    /// cluster with no CRD still authors rules perfectly well, it just has nowhere to put them yet.
    /// </summary>
    public bool CanApplyToCluster => _cluster.Capabilities.AlertRules && _onApply is not null;

    public bool CanApply => CanApplyToCluster && IsComplete;

    public string ApplyNotice => CanApplyToCluster
        ? "Goes through the ordinary apply route — server-side dry-run, then the diff, then apply. It "
          + "does not get a private path to the cluster."
        : "The PrometheusRule CRD is not installed on this cluster, so there is nothing here to apply "
          + "against. The manifest above is still the manifest.";

    [RelayCommand]
    private void Apply()
    {
        if (!CanApply)
            return;

        _onApply!(new ManifestBundle
        {
            Yaml = Manifest,
            Source = $"rule {AlertName.Trim()}",
            Namespace = NamespaceName.Trim(),
        });
    }

    // ── Reading the cluster ──────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _targeting = await _readTargeting();

        try
        {
            _namespaces = await _cluster.ListNamespacesAsync();
        }
        catch (Exception ex)
        {
            // Free text stays allowed, so a refused list costs the verdict and nothing else.
            _namespaceListRefusal =
                $"Kontena could not list this cluster's namespaces ({ex.Message}), so it cannot say "
                + "whether this one exists or is watched.";
        }

        NamespaceOptions.Clear();
        foreach (var ns in _namespaces.OrderBy(n => n.Name, StringComparer.Ordinal))
            NamespaceOptions.Add(new NamespaceOption(ns.Name, _targeting.Watches(ns), NoteFor(ns)));

        // The Prometheus' own namespace is the one answer that is always right, and typing it again
        // is the kind of work an editor should have done for you.
        if (NamespaceName.Length == 0 && _targeting.PrometheusNamespace is { Length: > 0 } home)
            NamespaceName = home;

        ObjectLabels.Clear();
        foreach (var (key, value) in _targeting.RequiredLabels)
            ObjectLabels.Add(new RuleLabelRow(key, value, Refresh, onRemove: null));

        FilterNamespaces();
        OnPropertyChanged(nameof(HasObjectLabels));
        OnPropertyChanged(nameof(SelectorNotice));
        OnPropertyChanged(nameof(SelectorNoticeIsWarning));
        OnPropertyChanged(nameof(SelectorNoticeBrushKey));
        OnPropertyChanged(nameof(NamespaceVerdict));
        OnPropertyChanged(nameof(NamespaceVerdictIsWarning));
        OnPropertyChanged(nameof(NamespaceVerdictBrushKey));
        Refresh();
    }

    private string NoteFor(KubeNamespace ns) => _targeting.Watches(ns) switch
    {
        true when _targeting.Scope == RuleNamespaceScope.OwnNamespace => "the namespace this Prometheus runs in",
        true => "matched by ruleNamespaceSelector",
        false => "not matched by ruleNamespaceSelector",
        null => string.Empty,
    };

    private void Refresh()
    {
        OnPropertyChanged(nameof(Rule));
        OnPropertyChanged(nameof(Manifest));
        OnPropertyChanged(nameof(Incomplete));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(SelectorNotice));
        OnPropertyChanged(nameof(SelectorNoticeIsWarning));
        OnPropertyChanged(nameof(SelectorNoticeBrushKey));
    }

    public void Dispose()
    {
        Check.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>One label or annotation row. Removable only where removing it is the author's call.</summary>
public sealed partial class RuleLabelRow : ObservableObject
{
    private readonly Action? _onChanged;
    private readonly Action<RuleLabelRow>? _onRemove;

    public RuleLabelRow(string key, string value, Action? onChanged, Action<RuleLabelRow>? onRemove)
    {
        _key = key;
        _value = value;
        _onChanged = onChanged;
        _onRemove = onRemove;
    }

    [ObservableProperty] private string _key;
    [ObservableProperty] private string _value;

    partial void OnKeyChanged(string value) => _onChanged?.Invoke();
    partial void OnValueChanged(string value) => _onChanged?.Invoke();

    /// <summary>False for the prefilled selector label: it is not yours to drop.</summary>
    public bool CanRemove => _onRemove is not null;

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);
}

/// <summary>One namespace in the typeahead, with the answer the field exists to give.</summary>
/// <param name="Name">The namespace.</param>
/// <param name="Watched">Whether rules here are evaluated; null when the selector could not be read.</param>
/// <param name="Note">One line of context under the name.</param>
public sealed record NamespaceOption(string Name, bool? Watched, string Note)
{
    public bool HasVerdict => Watched is not null;
    public string WatchedText => Watched is true ? "watched" : "not watched";
    public string WatchedBrushKey => Watched is true ? "Success" : "Warn";
    public bool HasNote => Note.Length > 0;
}

/// <summary>One button of the severity control.</summary>
public sealed partial class SeverityOption : ObservableObject
{
    private readonly Action<SeverityOption> _pick;

    internal SeverityOption(string name, bool on, Action<SeverityOption> Pick)
    {
        Name = name;
        _isOn = on;
        _pick = Pick;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isOn;

    [RelayCommand]
    private void Choose() => _pick(this);
}
