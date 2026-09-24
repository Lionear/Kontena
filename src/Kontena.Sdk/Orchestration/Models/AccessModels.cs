namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// One rule of a Role or ClusterRole: these verbs, on these resources (KON-474).
/// </summary>
public sealed record AccessRule
{
    public IReadOnlyList<string> Verbs { get; init; } = [];

    /// <summary>The API groups the resources live in; "" is the core group.</summary>
    public IReadOnlyList<string> ApiGroups { get; init; } = [];

    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>When set, the rule only covers these objects by name.</summary>
    public IReadOnlyList<string> ResourceNames { get; init; } = [];

    /// <summary>Paths such as <c>/healthz</c>, which only a ClusterRole can grant.</summary>
    public IReadOnlyList<string> NonResourceUrls { get; init; } = [];
}

/// <summary>A Role (namespaced) or a ClusterRole (<see cref="Namespace"/> null).</summary>
public sealed record AccessRole
{
    public required string Name { get; init; }

    public string? Namespace { get; init; }

    public bool IsClusterRole => Namespace is null;

    public IReadOnlyList<AccessRule> Rules { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>Who a binding grants to: a ServiceAccount, a User or a Group.</summary>
/// <param name="Namespace">Only ServiceAccounts have one.</param>
public sealed record AccessSubject(string Kind, string Name, string? Namespace = null);

/// <summary>
/// A RoleBinding (namespaced) or a ClusterRoleBinding (<see cref="Namespace"/> null). A RoleBinding
/// may point at a ClusterRole, and then grants that role's rules in its own namespace only.
/// </summary>
public sealed record AccessBinding
{
    public required string Name { get; init; }

    public string? Namespace { get; init; }

    public bool IsClusterBinding => Namespace is null;

    /// <summary>"Role" or "ClusterRole".</summary>
    public required string RoleKind { get; init; }

    public required string RoleName { get; init; }

    public IReadOnlyList<AccessSubject> Subjects { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>The roles and bindings in scope, read together so they can be joined (KON-474).</summary>
public sealed record AccessControl(IReadOnlyList<AccessRole> Roles, IReadOnlyList<AccessBinding> Bindings)
{
    public static AccessControl Empty { get; } = new([], []);
}
