using Avalonia;
using Avalonia.Headless;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Kontena's own application, headless.
/// <para>
/// The real one rather than a bare <see cref="Application"/> with a theme bolted on, because the
/// window under test reaches for the app's palette and icon resources — and a stand-in that happens to
/// have neither would pass a test about resources it never loaded. Only <c>Initialize</c> runs here;
/// the desktop branch of <c>OnFrameworkInitializationCompleted</c> is skipped, since a headless session
/// is not a classic desktop lifetime, so nothing touches the real settings file or a container engine.
/// </para>
/// </summary>
public static class HeadlessTestApp
{
    // Configures Kontena's own App rather than a subclass of it: AvaloniaXamlLoader resolves App.axaml
    // by the runtime type, so a subclass loads no resources at all — the palette silently falls back
    // and a test can pass against a window that is not the shipped one. There is a test for that.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Kontena.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
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
