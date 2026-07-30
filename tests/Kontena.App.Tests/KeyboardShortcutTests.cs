using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// What the keyboard does (KON-172). Before this there was not one key binding in the app: modals
/// could only be dismissed by finding the Cancel button.
/// </summary>
public sealed class KeyboardShortcutTests
{
    private static MainWindowViewModel Shell()
    {
        var path = Path.Combine(Path.GetTempPath(), "kontena-keys-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);

        return new MainWindowViewModel(new BackendRegistry([]), store, store.Load(), new FakeUpdateService());
    }

    private static ConfirmViewModel Confirm(Action onConfirm, Action onClose) =>
        new("Delete volume", "Gone for good.", "Delete",
            onConfirm: () => { onConfirm(); return Task.CompletedTask; },
            onClose: onClose,
            destructive: true);

    [Fact]
    public void Escape_closes_an_open_dialog()
    {
        var shell = Shell();
        shell.Dialog = Confirm(() => { }, () => { });

        shell.DismissCommand.Execute(null);

        Assert.Null(shell.Dialog);
        Assert.False(shell.IsDialogOpen);
    }

    [Fact]
    public void Escape_with_no_dialog_does_not_navigate()
    {
        // Escape means "dismiss what appeared". Making it go back as well means a mistimed Escape
        // leaves the page instead of the dialog you thought was open.
        var shell = Shell();
        shell.ShowAboutCommand.Execute(null);
        shell.ShowActivityCommand.Execute(null);

        shell.DismissCommand.Execute(null);

        Assert.True(shell.IsActivitySelected);
        Assert.True(shell.CanGoBack);
    }

    [Fact]
    public void Enter_runs_the_dialog_primary_action()
    {
        var confirmed = false;
        var shell = Shell();
        shell.Dialog = Confirm(() => confirmed = true, () => { });

        shell.ConfirmPrimaryCommand.Execute(null);

        Assert.True(confirmed);
    }

    [Fact]
    public void Enter_does_nothing_on_a_dialog_that_has_no_primary_action()
    {
        // Opt-in, so a dialog with a text area or several equal buttons is untouched rather than
        // guessing which button Enter means.
        var shell = Shell();
        shell.Dialog = new object();

        shell.ConfirmPrimaryCommand.Execute(null);

        Assert.NotNull(shell.Dialog);
    }

    [Fact]
    public void Enter_does_nothing_with_no_dialog_open()
    {
        var shell = Shell();

        shell.ConfirmPrimaryCommand.Execute(null);

        Assert.Null(shell.Dialog);
    }

    [Fact]
    public void A_busy_confirm_ignores_a_second_Enter()
    {
        // Holding Enter on a delete should not fire it twice; the guard is the button's own.
        var count = 0;
        var confirm = Confirm(() => count++, () => { });

        confirm.InvokePrimary();
        Assert.False(confirm.CanInvokePrimary);

        confirm.InvokePrimary();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Focusing_search_is_refused_where_there_is_nothing_to_search()
    {
        // A shortcut that focuses a disabled box is a shortcut that appears to do nothing (KON-164).
        var asked = false;
        var shell = Shell();
        shell.RequestFocusSearch = () => asked = true;

        shell.ShowAboutCommand.Execute(null);
        shell.FocusSearchCommand.Execute(null);

        Assert.False(shell.IsSearchEnabled);
        Assert.False(asked);
    }
}
