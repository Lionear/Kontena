namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// One resource type the cluster actually serves, as the API server describes it (KON-75).
/// <para>
/// Asked for rather than assumed. A cluster's set of kinds is not a constant: it depends on the
/// version, on what is installed, and on every operator that added its own. Anything Kontena hard-codes
/// is a kind it can show on clusters that do not have it and cannot show on clusters that do.
/// </para>
/// </summary>
public sealed record ApiResource
{
    /// <summary>Group, version and kind — how the resource is addressed everywhere else.</summary>
    public required GroupVersionKind Kind { get; init; }

    /// <summary>
    /// The path segment the API server uses, e.g. <c>networkpolicies</c>. From discovery, because
    /// pluralising a kind is a guess that a custom resource is free to contradict.
    /// </summary>
    public required string Plural { get; init; }

    /// <summary>Whether instances live in a namespace.</summary>
    public bool Namespaced { get; init; }

    /// <summary>
    /// The abbreviations the API server accepts for this kind, e.g. <c>cert</c> for a cert-manager
    /// Certificate (KON-455). Discovery has always carried these; nothing was reading them.
    /// <para>
    /// They matter for finding a kind rather than for addressing one: someone who half-remembers a
    /// custom resource often remembers what they type at kubectl, not the Kind the CRD declares.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ShortNames { get; init; } = [];

    /// <summary>
    /// The buckets the kind declares itself part of, e.g. <c>all</c>, or the name an operator groups
    /// its kinds under. Also from discovery, and also unread until now.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>What may be done with it, as the API server reports: <c>list</c>, <c>delete</c>, …</summary>
    public IReadOnlyList<string> Verbs { get; init; } = [];

    /// <summary>
    /// What this kind is for, in the CRD author's own words (KON-455). Empty for a built-in kind, and
    /// empty for a custom one whose author wrote no description or whose definition this user may not
    /// read — the picker shows the line only when there is one.
    /// <para>
    /// The one field here that is not free: it comes from the CustomResourceDefinition rather than from
    /// discovery. It is worth the call because it is the only thing on the row written for a human who
    /// does not already know the kind — which is the whole of what KON-455 reported.
    /// </para>
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// What installed this kind — an operator's name or a Helm release, from the definition's own
    /// labels. Empty when the definition says nothing about where it came from.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Whether it came from outside Kubernetes itself. Not a judgement about quality — it is how the
    /// list is grouped, because "the kinds my operators added" is the half a user is looking for.
    /// </summary>
    public bool IsCustom { get; init; }

    /// <summary>True when it can be listed at all; a resource that cannot is not worth offering.</summary>
    public bool CanList => Verbs.Contains("list", StringComparer.Ordinal);

    /// <summary>True when Kontena may offer to delete instances of it.</summary>
    public bool CanDelete => Verbs.Contains("delete", StringComparer.Ordinal);
}

/// <summary>One column of a resource listing, named by the server.</summary>
/// <param name="Name">Header text, e.g. <c>READY</c>.</param>
/// <param name="Priority">
/// 0 is what <c>kubectl get</c> shows; higher is what <c>-o wide</c> adds. Kept so the grid can hold
/// the extra columns back rather than drop them.
/// </param>
public sealed record ResourceColumn(string Name, int Priority);

/// <summary>One row: the object it points at, and the cells the server rendered for it.</summary>
public sealed record ResourceRow(ResourceRef Reference, IReadOnlyList<string> Cells);

/// <summary>
/// A listing of one kind, rendered by the API server rather than by Kontena.
/// <para>
/// This is the same Table the <c>kubectl get</c> output is built from, which is the point: the server
/// decides the columns, so a custom resource arrives with the columns its author declared and Kontena
/// needs to know nothing about it. It also means the grid says what someone typing <c>kubectl</c> at the
/// same cluster would see, rather than a second opinion about the same objects.
/// </para>
/// </summary>
public sealed record ResourceTable
{
    public IReadOnlyList<ResourceColumn> Columns { get; init; } = [];

    public IReadOnlyList<ResourceRow> Rows { get; init; } = [];

    /// <summary>An empty listing — what an unreachable or unserved kind yields.</summary>
    public static ResourceTable Empty { get; } = new();
}
