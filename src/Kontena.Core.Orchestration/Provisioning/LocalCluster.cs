namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// A cluster one of the provisioners made and still owns.
/// </summary>
/// <param name="Name">The name it was created with — what the tool answers with, and what deletes it.</param>
/// <param name="Provisioner">Which provisioner owns it, matching <see cref="IClusterProvisioner.Provisioner"/>.</param>
/// <param name="Context">
/// The kubeconfig context it writes, e.g. <c>kind-dev</c> for a kind cluster called <c>dev</c>. This is
/// the join to the rest of Kontena: the same string appears in the backend id
/// (<c>kubernetes:kind-dev</c>) that discovery already produces, so a page can tell which switcher
/// entry belongs to which local cluster without asking the tool again.
/// </param>
public sealed record LocalCluster(string Name, string Provisioner, string Context);
