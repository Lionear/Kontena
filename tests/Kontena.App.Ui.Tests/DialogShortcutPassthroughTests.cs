using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// Whether a key the shell does not want reaches whatever has focus (KON-201).
/// <para>
/// The complaint was that <c>ls</c> plus Enter did nothing in the terminal while <c>ls</c> plus
/// Ctrl+Enter worked. The mechanism is in Avalonia rather than in Kontena, and it is the opposite of
/// what <c>ShellActions</c> assumed: a <c>TopLevel</c>'s <c>KeyBindings</c> are matched <b>before</b>
/// the focused control sees the key, not after. A command that can always execute therefore eats its
/// key everywhere — and Escape and Enter carry no modifier, so everywhere included the terminal.
/// </para>
/// <para>
/// Measured here rather than trusted: this is the assumption the whole fix rests on, and the previous
/// version of it was written down as a comment and was wrong. A focusable <c>Border</c> stands in for
/// the terminal — a <c>TextBox</c> would not do, because it answers Enter itself and would hide the
/// very delivery being tested.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class DialogShortcutPassthroughTests(HeadlessSessionFixture headless)
{
    private sealed class FakeDialog : IPrimaryAction
    {
        public bool CanInvokePrimary => true;

        public void InvokePrimary()
        {
        }
    }

    /// <summary>The window as MainWindow builds it: the dialog gestures bound on the window itself.</summary>
    private static (Window Window, List<Key> Seen) Build(MainWindowViewModel shell)
    {
        var seen = new List<Key>();
        var target = new Border { Focusable = true };
        target.KeyDown += (_, e) => seen.Add(e.Key);

        var window = new Window { Width = 300, Height = 200, Content = target, DataContext = shell };
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = shell.ConfirmPrimaryCommand });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = shell.DismissCommand });

        window.Show();
        target.Focus();
        Dispatcher.UIThread.RunJobs();

        return (window, seen);
    }

    [Fact]
    public Task With_no_dialog_open_enter_and_escape_reach_the_focused_control() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, seen) = Build(new MainWindowViewModel());

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Dispatcher.UIThread.RunJobs();

                Assert.Contains(Key.Enter, seen);
                Assert.Contains(Key.Escape, seen);
            },
            CancellationToken.None);

    [Fact]
    public Task An_open_dialog_takes_enter_before_the_focused_control_sees_it() =>
        headless.Session.Dispatch(
            () =>
            {
                // The other half, and what makes this a guard rather than a removal: while a modal is
                // up those keys belong to the shell.
                var (window, seen) = Build(new MainWindowViewModel { Dialog = new FakeDialog() });

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
                Dispatcher.UIThread.RunJobs();

                Assert.DoesNotContain(Key.Enter, seen);
            },
            CancellationToken.None);
}
