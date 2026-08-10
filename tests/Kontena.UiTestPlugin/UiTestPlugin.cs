using Avalonia.Controls;
using Kontena.Sdk;

namespace Kontena.UiTestPlugin;

/// <summary>
/// A plugin that contributes pages and no backend at all (KON-331) — the shape Manifest Studio has.
/// Its whole job is to prove that "no <c>IEnginePlugin</c>" is a plugin rather than a rejection.
/// </summary>
public sealed class UiTestPlugin : IUiPlugin
{
    public EngineManifest Manifest => new()
    {
        Id = "com.kontena.uitest",
        Name = "UI Test Plugin",
        Version = "1.0.0",
        Author = "Kontena",
        Description = "Fixture for the UI-contribution seam.",
        MinSdkVersion = "0.1.0",
    };

    public IEnumerable<PluginPage> GetPages() =>
    [
        new PluginPage("editor", "Editor", "IconBox", () => new TextBlock { Text = "Editor" }),
    ];
}
