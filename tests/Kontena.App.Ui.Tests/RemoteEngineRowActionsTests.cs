using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Models;
using Kontena.Sdk.Models;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The Edit and Remove buttons on a remote engine row are actually connected (KON-264).
/// <para>
/// Against the rendered view rather than the view model, because the view model half is already
/// covered and was never the risk. These two buttons reach their commands through
/// <c>$parent[ItemsControl].((vm:SettingsViewModel)DataContext)</c> — a path out of the row's own
/// data context.
/// </para>
/// <para>
/// <b>What the compiler already catches, measured:</b> the template carries an <c>x:DataType</c>, so
/// a misspelt command name fails the build with <c>AVLN2000</c> rather than shipping. That is worth
/// knowing, because it is the reason this test does <i>not</i> need to exist for that case — and
/// claiming otherwise would be a comment that is simply untrue.
/// </para>
/// <para>
/// <b>What it does not catch, also measured:</b> the ancestor lookup itself. Point
/// <c>$parent</c> at a <c>StackPanel</c> instead and it compiles cleanly, finds a control whose data
/// context is a row rather than the page, and hands the button a null command — which renders,
/// enables, and does nothing when pressed. Nor does the compiler have anything to say about a
/// missing <c>CommandParameter</c>, where the command runs against null and returns. Both are the
/// dead-button symptom this ticket started from, and the only way to see either is to ask the built
/// control what it ended up bound to.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class RemoteEngineRowActionsTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Fact]
    public Task Both_row_actions_are_bound_to_a_command_and_carry_their_row() => Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-row-actions-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings
            {
                RemoteEngines =
                [
                    new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 22, "deploy"),
                ],
            };

            store.Save(settings);

            var page = new SettingsViewModel(
                store, settings, [],
                new SettingsContext
                {
                    Autostart = new UnsupportedAutostart(),
                    Secrets = new UnavailableSecretStore(),
                })
            {
                Category = "engines",
            };

            var window = new Window
            {
                Width = 1200,
                Height = 900,
                Content = new SettingsView { DataContext = page },
            };

            window.Show();

            for (var i = 0; i < 5; i++)
                Dispatcher.UIThread.RunJobs();

            var actions = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Content as string is "Edit" or "Remove")
                .ToList();

            Assert.Equal(2, actions.Count);

            foreach (var button in actions)
            {
                Assert.NotNull(button.Command);

                // Without the row, the command runs against null and returns immediately — a button
                // that looks connected and still does nothing.
                Assert.IsType<RemoteEngineRow>(button.CommandParameter);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }, CancellationToken.None);
}
