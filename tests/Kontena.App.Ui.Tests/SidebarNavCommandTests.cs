using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// One click on a sidebar entry is one navigation (KON-413).
/// <para>
/// The diagnostics on that report logged every <c>navigate to</c> twice, which was two marks in the
/// code rather than two navigations — but "the command fires twice" was the reading it invited, and
/// nothing here held the binding still enough to rule it out. This does: the entry is one Button
/// bound to one command, so a press and release run it once, with its own key.
/// </para>
/// <para>
/// Counted rather than measured, per the rule for this assembly: what is asserted is the invocation,
/// not what the button looks like while it happens.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class SidebarNavCommandTests(HeadlessSessionFixture headless)
{
    /// <summary>Stands in for the shell's own navigate command, so the count is the binding's.</summary>
    private sealed class CountingCommand : ICommand
    {
        public List<object?> Ran { get; } = [];

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            Ran.Add(parameter);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public Task Clicking_an_entry_runs_its_command_once() =>
        headless.Session.Dispatch(
            () =>
            {
                var vm = new MainWindowViewModel { IsBackendDown = false };
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // Whichever nav is up: both are drawn by the one NavItem template, so a click that
                // ran twice would be a property of the template rather than of the cluster.
                var item = NavItems(vm).First(i => i.Key == "images");
                var button = window.GetVisualDescendants().OfType<Button>()
                    .First(b => ReferenceEquals(b.DataContext, item));

                // Counted on the button rather than through the item, which raises nothing when its
                // command is replaced. That the button binds the shell's command at all is the
                // assertion below; this one is about how many times one click runs it.
                var counting = new CountingCommand();
                button.Command = counting;
                Dispatcher.UIThread.RunJobs();

                Click(button);

                Assert.Equal(["images"], counting.Ran);
            },
            CancellationToken.None);

    [Fact]
    public Task Every_entry_carries_its_own_key_and_one_command() =>
        headless.Session.Dispatch(
            () =>
            {
                var vm = new MainWindowViewModel();
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // One Button per entry, and its parameter is that entry's key: two buttons over one
                // item, or a stale parameter, is the other way a click becomes two navigations.
                foreach (var item in NavItems(vm))
                {
                    var buttons = window.GetVisualDescendants().OfType<Button>()
                        .Where(b => ReferenceEquals(b.DataContext, item))
                        .ToList();

                    Assert.Single(buttons);
                    Assert.Equal(item.Key, buttons[0].CommandParameter);
                    Assert.Same(vm.NavigateCommand, buttons[0].Command);
                }
            },
            CancellationToken.None);

    /// <summary>Every sidebar entry, flattened out of the groups the shell draws them in.</summary>
    private static IEnumerable<NavItem> NavItems(MainWindowViewModel vm) =>
        vm.NavGroups.SelectMany(g => g.Items);

    /// <summary>
    /// Press and release over the middle of the control, which is where a Button runs its command.
    /// The position is the middle rather than the origin because the release is hit-tested: a corner
    /// lands on the edge and the press is dropped without a click, which looks exactly like a binding
    /// that never ran.
    /// </summary>
    private static void Click(Control target)
    {
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var middle = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);

        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, target, middle, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, target, middle, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        Dispatcher.UIThread.RunJobs();
    }
}
