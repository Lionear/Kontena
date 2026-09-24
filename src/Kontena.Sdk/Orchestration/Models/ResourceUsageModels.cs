namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// How a link between two objects was established (KON-455).
/// <para>
/// Carried rather than discarded because the answer is only as trustworthy as the way it was found,
/// and these ways differ: an ownerReference is what Kubernetes itself records, while a naming
/// convention is a guess that happens to be right most of the time. Every comparable tool shows
/// relations without saying which of the two it is looking at; a user cannot check a claim whose
/// basis is not on screen.
/// </para>
/// </summary>
public enum UsageEvidence
{
    /// <summary>
    /// The object carries an ownerReference to this one — Kubernetes' own record of what created what,
    /// and the only signal here that cannot be a coincidence.
    /// </summary>
    OwnerReference,

    /// <summary>
    /// The object mounts, or reads environment from, a ConfigMap or Secret that this object owns. Two
    /// hops, both of them exact: the config object's ownerReference, then the pod spec's reference to
    /// it by name. This is the operator pattern — a custom resource emits credentials and the workload
    /// consumes them — and it is what "used by" usually means in practice.
    /// </summary>
    Mount,

    /// <summary>
    /// An annotation on the object names this one, under a key belonging to this object's API group.
    /// A convention rather than a record, so it is reported last and always with the key it was read
    /// from — the group in the key is what keeps it from matching any annotation that happens to hold
    /// the same string.
    /// </summary>
    Annotation,
}

/// <summary>
/// One object that uses, or belongs to, another (KON-455).
/// </summary>
/// <param name="User">The object at the other end — what to open when the row is clicked.</param>
/// <param name="Evidence">How the link was found.</param>
/// <param name="Detail">
/// The link in the words of the cluster: the annotation key, or the ConfigMap that carried it. Shown
/// next to the row so the claim can be checked rather than believed.
/// </param>
/// <param name="OwnedByTarget">
/// True when the object was created by the one being asked about, rather than merely referring to it.
/// The same list holds both directions and they do not mean the same thing — "this created that" is a
/// different fact from "that uses this".
/// </param>
public sealed record ResourceUsage(
    ResourceRef User,
    UsageEvidence Evidence,
    string Detail,
    bool OwnedByTarget = false);
