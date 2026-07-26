namespace Kontena.Core.Tooling;

/// <summary>
/// The external tools Kontena drives, described once.
/// </summary>
/// <remarks>
/// Only package names that are certain appear here. Where a tool is not in a distribution's own
/// repositories — kind and minikube are not in Debian's or Fedora's — the hint is
/// <see cref="PackageManager.Manual"/> with a link, rather than a guessed package name. A hint that
/// does not work is worse than no hint: it sends someone off to debug our typo instead of installing
/// their tool.
/// </remarks>
public static class KnownTools
{
    public static readonly ExternalTool Kind = new(
        "kind",
        "kind",
        ["version"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "kind"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Kubernetes.kind"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "kind"]),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://kind.sigs.k8s.io/docs/user/quick-start/#installation",
    };

    public static readonly ExternalTool Minikube = new(
        "minikube",
        "minikube",
        ["version", "--short"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "minikube"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Kubernetes.minikube"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "minikube"]),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://minikube.sigs.k8s.io/docs/start/",
    };

    public static readonly ExternalTool Kubectl = new(
        "kubectl",
        "kubectl",
        // Plain, not `-o yaml`: the structured form starts with a bare `clientVersion:` key, so the
        // first line — which is what a version is read from — carries no version at all. The default
        // output leads with `Client Version: v1.34.9`. Verified against kubectl 1.34.
        ["version", "--client=true"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "kubectl"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Kubernetes.kubectl"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "kubectl"]),
            new InstallHint(PackageManager.Dnf, "dnf", ["install", "kubernetes-client"], RequiresElevation: true),
            new InstallHint(PackageManager.Pacman, "pacman", ["-S", "kubectl"], RequiresElevation: true),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://kubernetes.io/docs/tasks/tools/",
    };

    public static readonly ExternalTool Helm = new(
        "helm",
        "helm",
        ["version", "--short"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "helm"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Helm.Helm"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "helm"]),
            new InstallHint(PackageManager.Pacman, "pacman", ["-S", "helm"], RequiresElevation: true),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://helm.sh/docs/intro/install/",
    };

    public static readonly ExternalTool Kustomize = new(
        "kustomize",
        "kustomize",
        ["version"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "kustomize"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "Kubernetes.kustomize"]),
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "kustomize"]),
            new InstallHint(PackageManager.Pacman, "pacman", ["-S", "kustomize"], RequiresElevation: true),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        // Not having it is fine: the renderer falls back to `kubectl kustomize`, which every kubectl
        // carries. This entry exists so the fallback can be explained rather than silently taken.
        DocumentationUrl = "https://kubectl.docs.kubernetes.io/installation/kustomize/",
    };

    public static readonly ExternalTool Podman = new(
        "podman",
        "podman",
        ["--version"],
        [
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "podman"]),
            new InstallHint(PackageManager.Winget, "winget", ["install", "-e", "--id", "RedHat.Podman"]),
            new InstallHint(PackageManager.Dnf, "dnf", ["install", "podman"], RequiresElevation: true),
            new InstallHint(PackageManager.Apt, "apt-get", ["install", "-y", "podman"], RequiresElevation: true),
            new InstallHint(PackageManager.Pacman, "pacman", ["-S", "podman"], RequiresElevation: true),
            new InstallHint(PackageManager.Manual, "", []),
        ])
    {
        DocumentationUrl = "https://podman.io/docs/installation",
    };

    /// <summary>Everything above, for a settings page that wants to show what is present.</summary>
    public static IReadOnlyList<ExternalTool> All => [Kind, Minikube, Kubectl, Helm, Kustomize, Podman];
}
