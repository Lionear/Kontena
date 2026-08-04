using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// A bare Avalonia application, headless. The plugin has no App.axaml of its own — it is loaded into
/// the host's — so the only thing this needs to prove is that AvaloniaEdit renders under a plain Fluent
/// theme, not that any of Kontena.App's resources are present.
/// </summary>
public sealed class SpikeApp : Application
{
    // AvaloniaEdit ships no default template unless this is merged in (DataTray.App.axaml already
    // does this against the same package version) — without it TextEditor renders with an empty
    // visual tree instead of throwing, which is exactly the silent-break risk §11 flagged.
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Kontena.Plugins.ManifestStudio.Tests"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
    }
}

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SpikeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Owns the one headless session this assembly runs in (mirrors Kontena.App.Ui.Tests, KON-198).</summary>
public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    public void Dispose() => Session.Dispose();
}

[CollectionDefinition(HeadlessTests.Name)]
public sealed class HeadlessTests : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "avalonia";
}
