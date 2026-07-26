using System.Runtime.InteropServices;

namespace Kontena.Core.Tooling;

/// <summary>
/// This machine, in the words release publishers use in their file names. kind and minikube both
/// ship <c>&lt;tool&gt;-&lt;os&gt;-&lt;arch&gt;</c>, so getting these two strings right is most of
/// picking the right download.
/// </summary>
public static class ToolPlatform
{
    /// <summary>"linux", "darwin" or "windows" — the spelling used in release assets.</summary>
    public static string Os =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "darwin" :
        "linux";

    /// <summary>"amd64" or "arm64". Anything else is not a platform these tools publish for.</summary>
    public static string? Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => null,
    };

    /// <summary>
    /// Whether Kontena can fetch binaries for this machine at all. On anything else — a 32-bit ARM
    /// board, say — the honest answer is the package manager or the documentation, not a download
    /// that will not run.
    /// </summary>
    public static bool CanDownload => Architecture is not null;
}
