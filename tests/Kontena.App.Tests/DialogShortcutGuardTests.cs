using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// Escape and Enter are only claimed while a dialog is open (KON-201).
/// <para>
/// Both are bound on the window and carry no modifier, and Avalonia's <c>KeyBinding</c> runs the
/// command and <i>then</i> marks the key handled — so a command that can always execute swallows its
/// key everywhere, including in a focused terminal. The guard therefore has to live in
/// <c>CanExecute</c>, which is what the binding consults, and not inside the method body where it was.
/// </para>
/// </summary>
public sealed class DialogShortcutGuardTests
{
    /// <summary>A dialog with a primary action, as the confirm ones are.</summary>
    private sealed class FakeDialog : IPrimaryAction
    {
        public bool CanInvokePrimary { get; init; } = true;

        public int Invocations { get; private set; }

        public void InvokePrimary() => Invocations++;
    }

    [Fact]
    public void With_no_dialog_open_neither_key_is_claimed()
    {
        var shell = new MainWindowViewModel();

        // False, not "true but does nothing": the difference is whether the key reaches the terminal.
        Assert.False(shell.DismissCommand.CanExecute(null));
        Assert.False(shell.ConfirmPrimaryCommand.CanExecute(null));
    }

    [Fact]
    public void A_dialog_takes_them_both_back()
    {
        var shell = new MainWindowViewModel { Dialog = new FakeDialog() };

        Assert.True(shell.DismissCommand.CanExecute(null));
        Assert.True(shell.ConfirmPrimaryCommand.CanExecute(null));
    }

    [Fact]
    public void Closing_the_dialog_gives_the_keys_back()
    {
        // The notification half: without it the commands keep whatever they answered when the window
        // was built, and the keys would be claimed — or not — forever after.
        var shell = new MainWindowViewModel { Dialog = new FakeDialog() };

        shell.Dialog = null;

        Assert.False(shell.DismissCommand.CanExecute(null));
        Assert.False(shell.ConfirmPrimaryCommand.CanExecute(null));
    }

    [Fact]
    public void Enter_runs_the_primary_action_of_the_open_dialog()
    {
        var dialog = new FakeDialog();
        var shell = new MainWindowViewModel { Dialog = dialog };

        shell.ConfirmPrimaryCommand.Execute(null);

        Assert.Equal(1, dialog.Invocations);
    }

    [Fact]
    public void A_dialog_whose_primary_action_is_not_ready_still_claims_the_key_but_does_nothing()
    {
        // Mid-save, or a required field still empty. The key is claimed because a modal is up and
        // nothing behind it can have focus — but it must not fire the action.
        var dialog = new FakeDialog { CanInvokePrimary = false };
        var shell = new MainWindowViewModel { Dialog = dialog };

        Assert.True(shell.ConfirmPrimaryCommand.CanExecute(null));

        shell.ConfirmPrimaryCommand.Execute(null);

        Assert.Equal(0, dialog.Invocations);
    }

    [Fact]
    public void A_dialog_without_a_primary_action_is_still_dismissable()
    {
        // A YAML editor has no one obvious action, so Enter belongs to the text box — but Escape still
        // closes it (KON-172).
        var shell = new MainWindowViewModel { Dialog = new object() };

        Assert.True(shell.DismissCommand.CanExecute(null));

        shell.DismissCommand.Execute(null);

        Assert.Null(shell.Dialog);
    }
}
