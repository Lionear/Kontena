using System.Runtime.CompilerServices;
using Kontena.App.Services;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Keeps this assembly off the real settings file (KON-433).
/// <para>
/// Runs before the first test, so a <c>new MainWindowViewModel { … }</c> anywhere in here reads an
/// empty file in a temp directory rather than the settings of whoever is running the suite. Nothing
/// to remember per test class: a test that wants a setting to hold a particular value still hands the
/// shell a <see cref="SettingsStore"/> of its own.
/// </para>
/// </summary>
internal static class SettingsSandbox
{
    [ModuleInitializer]
    internal static void Redirect() => SettingsStore.RedirectToTempForTests();
}
