using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// Settings opens over the app rather than instead of it (KON-437).
/// <para>
/// It used to be a navigation destination, which meant going there left the page you were reading —
/// and coming back was a Back press to a place you had not chosen to leave. As a dialog it changes
/// nothing behind it, and Escape is the way out.
/// </para>
/// </summary>
public sealed class SettingsDialogTests : IDisposable
{
    // A store on a temp path, never the parameterless one: that writes into the real user profile
    // (KON-433).
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-settings-dialog-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private MainWindowViewModel Shell()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true };
        store.Save(settings);

        return new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService());
    }

    [Fact]
    public void Opening_it_leaves_the_page_behind_it_alone()
    {
        var shell = Shell();
        var before = shell.CurrentPage;

        shell.ShowSettingsCommand.Execute(null);

        Assert.True(shell.IsSettingsOpen);
        Assert.Same(before, shell.CurrentPage);

        // Not a history stop: there is nothing to go back from, because nothing was left.
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public void Escape_closes_it()
    {
        var shell = Shell();
        shell.ShowSettingsCommand.Execute(null);

        Assert.True(shell.DismissCommand.CanExecute(null));
        shell.DismissCommand.Execute(null);

        Assert.False(shell.IsSettingsOpen);
    }

    /// <summary>
    /// The whole reason Settings is its own overlay instead of the shared modal slot: it puts
    /// confirmations in that slot itself — switching an adapter off asks first — so a dialog that
    /// replaced Settings would take away the thing the question was about.
    /// </summary>
    [Fact]
    public void A_confirmation_raised_from_it_sits_over_it_rather_than_replacing_it()
    {
        var shell = Shell();
        shell.ShowSettingsCommand.Execute(null);

        shell.SettingsPage!.RequestConfirm!(new ConfirmRequest(
            "Turn off", "It stops being offered.", "Turn off", () => Task.CompletedTask));

        Assert.True(shell.IsDialogOpen);
        Assert.True(shell.IsSettingsOpen);

        // First Escape answers the question…
        shell.DismissCommand.Execute(null);
        Assert.False(shell.IsDialogOpen);
        Assert.True(shell.IsSettingsOpen);

        // …the second closes what it was about.
        shell.DismissCommand.Execute(null);
        Assert.False(shell.IsSettingsOpen);
    }

    [Fact]
    public void The_close_button_closes_it()
    {
        var shell = Shell();
        shell.ShowSettingsCommand.Execute(null);

        shell.SettingsPage!.CloseCommand.Execute(null);

        Assert.False(shell.IsSettingsOpen);
    }
}
