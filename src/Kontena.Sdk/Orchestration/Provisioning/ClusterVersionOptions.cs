namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// The Kubernetes versions a provisioner is willing to be asked for (KON-144).
/// <para>
/// Two fields rather than one list because the tools differ in what they will tell us. minikube can be
/// asked what it supports and its answer is ordered newest-first, so its default is knowable; kind's
/// default lives in the node image its release was built against and is not printed anywhere before a
/// create. Saying "Default for this release" where we do not know, and naming it where we do, is the
/// difference between a label and a guess.
/// </para>
/// </summary>
/// <param name="Offered">
/// Concrete versions, newest first, without the default entry — the caller adds that. Empty means the
/// tool is absent or would not say, and the form then offers the default alone.
/// </param>
/// <param name="Default">The version the tool would pick when asked for none, or null when unknown.</param>
public sealed record ClusterVersionOptions(IReadOnlyList<string> Offered, string? Default = null)
{
    /// <summary>Nothing to offer — an absent tool, or one that answered with something unreadable.</summary>
    public static ClusterVersionOptions None { get; } = new([]);
}
