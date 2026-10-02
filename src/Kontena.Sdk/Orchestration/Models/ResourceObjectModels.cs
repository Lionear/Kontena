namespace Kontena.Sdk.Orchestration.Models;

/// <summary>One named value about an object: a printer column's cell, or a field of its status.</summary>
public sealed record ResourceField(string Name, string Value);

/// <summary>
/// One entry of an object's <c>status.conditions</c> (KON-483). The shape is a Kubernetes convention
/// rather than a schema, which is what lets a page read it off any kind: a custom resource that follows
/// it — CloudNativePG, cert-manager, Cluster API and most operators do — gets its conditions shown
/// without anyone having taught Kontena what they mean.
/// </summary>
public sealed record ResourceCondition(
    string Type, string Status, string Reason, string Message, DateTimeOffset? LastTransition);

/// <summary>
/// What any object can say about itself without a model of its kind (KON-483).
/// <para>
/// The detail page for a kind nobody modelled is built from this. Everything here is either metadata,
/// which every object has, or something the API server renders the same way <c>kubectl</c> does — the
/// printer columns a CRD author declared — or the conventions an object's status follows by agreement.
/// Nothing in it knows what a CloudNativePG Cluster is.
/// </para>
/// </summary>
public sealed record ResourceObject
{
    public required ResourceRef Reference { get; init; }

    public DateTimeOffset? Created { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The objects this one says it belongs to — its ownerReferences, as references a page can open.
    /// An owner always lives in the same namespace as what it owns, or is cluster-scoped.
    /// </summary>
    public IReadOnlyList<ResourceRef> Owners { get; init; } = [];

    /// <summary>
    /// The cells the API server renders for this object, by column — what <c>kubectl get</c> prints,
    /// including a CRD's <c>additionalPrinterColumns</c>. The name column is left out: it is the title.
    /// </summary>
    public IReadOnlyList<ResourceField> Columns { get; init; } = [];

    /// <summary>
    /// The top-level scalar fields of <c>status</c> — <c>phase</c>, <c>readyInstances</c>,
    /// <c>currentPrimary</c>. Nested structures are left to the YAML: there is no generic way to
    /// flatten them that reads better than the YAML already does.
    /// </summary>
    public IReadOnlyList<ResourceField> Status { get; init; } = [];

    public IReadOnlyList<ResourceCondition> Conditions { get; init; } = [];
}
