using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Who may do what (KON-474). The Resources browser shows Roles and Bindings as four separate
/// tables, which leaves the join to the reader; this page does the join. One row per subject per
/// binding — the grant as Kubernetes evaluates it — with the role's rules one click away.
/// </summary>
public partial class ClusterAccessViewModel : ClusterListPageViewModel<AccessGrantRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;

    public ClusterAccessViewModel(IClusterEngine cluster, string? @namespace)
        : base(cluster, null, @namespace,
            unwatchable: "Roles and bindings are four kinds at once, so this page updates when you refresh it.")
    {
        _cluster = cluster;
        _namespace = @namespace;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search subjects, roles, resources…";

    protected override async Task<IReadOnlyList<AccessGrantRow>> LoadRowsAsync(CancellationToken ct)
    {
        var access = await _cluster.GetAccessControlAsync(_namespace, ct);
        return Grants(access);
    }

    /// <summary>
    /// Join bindings to the roles they name. A RoleBinding's roleRef is resolved in its own namespace
    /// when it names a Role, cluster-wide when it names a ClusterRole — the same lookup the API
    /// server's authorizer does.
    /// </summary>
    internal static IReadOnlyList<AccessGrantRow> Grants(AccessControl access)
    {
        var roles = access.Roles.ToDictionary(r => (r.Namespace, r.Name));

        return
        [
            .. access.Bindings
                .SelectMany(b => b.Subjects.Select(s => new AccessGrantRow(
                    s, b,
                    roles.GetValueOrDefault((b.RoleKind == "ClusterRole" ? null : b.Namespace, b.RoleName)))))
                .OrderBy(r => r.SubjectKind, StringComparer.Ordinal)
                .ThenBy(r => r.Subject, StringComparer.OrdinalIgnoreCase),
        ];
    }

    // Rules too, so "secrets" finds everything that can touch a secret. A wildcard rule does not match
    // every term: it would put cluster-admin on top of every search, including a search for a name.
    protected override bool Matches(AccessGrantRow row, string term) =>
        Contains(row.Subject, term) || Contains(row.SubjectDetail, term) || Contains(row.Role, term)
        || Contains(row.Binding, term) || Contains(row.Scope, term)
        || row.Rules.Any(r => Contains(r.Resources, term) || Contains(r.Verbs, term) || Contains(r.Names, term));

    protected override IReadOnlyDictionary<string, Func<AccessGrantRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<AccessGrantRow, IComparable>>(StringComparer.Ordinal)
        {
            ["SUBJECT"] = r => r.Subject,
            ["ROLE"] = r => r.Role,
            ["SCOPE"] = r => r.Scope,
            ["BINDING"] = r => r.Binding,
        };
}

/// <summary>One subject granted one role by one binding.</summary>
public sealed partial class AccessGrantRow : ObservableObject
{
    public AccessGrantRow(AccessSubject subject, AccessBinding binding, AccessRole? role)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(binding);

        SubjectKind = subject.Kind;
        Subject = subject.Name;
        SubjectDetail = subject.Namespace is { } ns ? $"{subject.Kind} · namespace {ns}" : subject.Kind;

        Role = binding.RoleName;
        RoleKind = binding.RoleKind;

        // Where the grant applies — a RoleBinding to a ClusterRole is still only its own namespace.
        Scope = binding.Namespace is { } where ? where : "Cluster-wide";

        Binding = binding.Name;
        BindingKind = binding.IsClusterBinding ? "ClusterRoleBinding" : "RoleBinding";

        RoleMissing = role is null;
        Rules = role is null ? [] : [.. role.Rules.Select(r => new AccessRuleRow(r))];
        RulesLabel = RoleMissing ? "role not found"
            : Rules.Count == 1 ? "1 rule" : $"{Rules.Count} rules";
    }

    public string SubjectKind { get; }

    public string Subject { get; }

    /// <summary>The kind, and the namespace a ServiceAccount lives in.</summary>
    public string SubjectDetail { get; }

    public string Role { get; }

    public string RoleKind { get; }

    public string Scope { get; }

    public string Binding { get; }

    public string BindingKind { get; }

    /// <summary>
    /// The binding names a role that does not exist. Kubernetes allows that and grants nothing, which
    /// is exactly the thing worth seeing when a permission "should" be there.
    /// </summary>
    public bool RoleMissing { get; }

    public IReadOnlyList<AccessRuleRow> Rules { get; }

    public string RulesLabel { get; }

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>One rule, flattened into the two things it answers: on what, and which verbs.</summary>
public sealed class AccessRuleRow(AccessRule rule)
{
    /// <summary>
    /// Resources qualified by group the way kubectl spells them (<c>deployments.apps</c>), or the
    /// non-resource URLs for a rule that has those instead.
    /// </summary>
    public string Resources { get; } = rule.NonResourceUrls.Count > 0
        ? string.Join(", ", rule.NonResourceUrls)
        : string.Join(", ", rule.Resources
            .SelectMany(res => (rule.ApiGroups.Count == 0 ? [""] : rule.ApiGroups)
                .Select(g => g is "" or "*" ? res : $"{res}.{g}"))
            .Distinct(StringComparer.Ordinal));

    public string Verbs { get; } = string.Join(", ", rule.Verbs);

    /// <summary>The objects the rule is narrowed to by name; empty when it covers them all.</summary>
    public string Names { get; } = rule.ResourceNames.Count > 0 ? "only " + string.Join(", ", rule.ResourceNames) : string.Empty;

    public bool HasNames => Names.Length > 0;
}
