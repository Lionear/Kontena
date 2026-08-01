namespace Kontena.Sdk.Tooling;

/// <summary>
/// A specific release of a tool that Kontena can fetch itself, for machines with no package manager.
/// </summary>
/// <param name="Tool">What is being installed.</param>
/// <param name="Version">The release, as the publisher names it — e.g. <c>v0.31.0</c>.</param>
/// <param name="Url">Where the binary for this platform lives.</param>
/// <param name="Sha256">The digest the publisher published for that exact file, lower-case hex.</param>
/// <remarks>
/// The digest is not optional and there is no constructor without it. Fetching an executable over the
/// network and running it is the one thing in Kontena that could quietly become someone else's code —
/// a download path that can skip verification is a supply chain we opened ourselves.
/// </remarks>
public sealed record ToolDownload(ExternalTool Tool, string Version, Uri Url, string Sha256)
{
    /// <summary>What the file is called once it lands. Windows needs the extension to run it.</summary>
    public string FileName => OperatingSystem.IsWindows() ? $"{Tool.Executable}.exe" : Tool.Executable;
}
