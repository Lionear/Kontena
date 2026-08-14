using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Apple;

/// <summary>
/// Registers Apple's native <c>container</c> runtime as a backend, alongside Docker and Podman
/// (KON-31).
/// </summary>
public sealed class AppleEngineProvider : IBackendProvider
{
    private readonly IToolRunner _runner;

    /// <summary>The parameterless form is what the app registers; the runner overload is for the tests,
    /// which script a fake CLI instead of requiring macOS 26 and a running apiserver.</summary>
    public AppleEngineProvider() : this(new ToolRunner()) { }

    internal AppleEngineProvider(IToolRunner runner) => _runner = runner;

    public string Backend => "apple";

    /// <summary>
    /// Apple's own name for the product is just "container", which is unusable on a screen full of
    /// containers. "Apple container" is what its documentation calls it in prose and what the roadmap
    /// row on the onboarding screen has always said.
    /// </summary>
    public string DisplayName => "Apple container";

    public string Chip => "A";

    public BackendChipStyle? ChipStyle => new(AppleBrand.Glyph, AppleBrand.Accent);

    public BackendKind Kind => BackendKind.Engine;

    /// <summary>
    /// Whether there is any sign of this runtime on this machine — the binary being on disk, not the
    /// apiserver answering. An installed-but-stopped runtime must still be listed: "it is here, it is
    /// not running" is exactly what someone opens the switcher to find out (KON-255), and
    /// <c>container system start</c> is an ordinary thing to have not done yet.
    /// <para>
    /// The OS check comes first and is not merely an optimisation. <c>container</c> runs each container
    /// in its own lightweight VM through the macOS virtualization framework; there is no Windows or
    /// Linux build to find. Without this, the switcher on those platforms would list a backend that
    /// cannot exist there.
    /// </para>
    /// </summary>
    public bool IsInstalled =>
        OperatingSystem.IsMacOS() &&
        ToolLocator.Locate(AppleTool.Definition.Executable, AppleTool.Definition.ExtraSearchPaths) is not null;

    public IBackend CreateBackend() => new AppleEngine(new AppleCli(_runner), Backend, DisplayName);
}
