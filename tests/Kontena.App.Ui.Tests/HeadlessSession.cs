using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Kontena.App.Ui.Tests;

/// <summary>The least app a control can be laid out in: a theme, and nothing else.</summary>
public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>
/// Owns the one headless session this assembly runs in, and — the part that matters — shuts it down
/// again. The session runs its own dispatcher thread; left running it keeps the test host alive after
/// the last test, which hangs the whole assembly rather than just the class that started it.
/// <para>
/// One session for every test class, through a collection fixture: a second Avalonia application in
/// the same process is not a thing, and the classes that need one are not related beyond needing it.
/// </para>
/// </summary>
public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// The xUnit collection every UI test belongs to, so they share the one application. Named as a
/// marker rather than as a collection type — it holds nothing.
/// </summary>
[CollectionDefinition(HeadlessTests.Name)]
public sealed class HeadlessTests : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "avalonia";
}
