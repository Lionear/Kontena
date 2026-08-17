using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The namespace picker in the command bar (KON-373): a fixed width, and a filter.
/// <para>
/// The width is the bug that was reported — the ComboBox before this measured itself on the selected
/// name, so picking a longer namespace widened the picker and pushed the refresh and theme buttons
/// along with it. Widths are compared against each other rather than against a number: headless
/// Avalonia measures text with a stub glyph, so what a name is worth in pixels here means nothing,
/// but "the same before and after" is the regression itself.
/// </para>
/// <para>
/// The rest is the filter's edges. An AutoCompleteBox is a text box that happens to suggest, and it
/// drops its own selection on every keystroke; these cover that half-typed text never reaches
/// <c>SelectedNamespace</c> and never survives losing focus.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class NamespacePickerTests(HeadlessSessionFixture headless)
{
    private const string All = "All namespaces";

    private static (Window Window, AutoCompleteBox Picker, MainWindowViewModel Vm) Build()
    {
        var vm = new MainWindowViewModel { IsClusterMode = true };
        foreach (var ns in new[] { All, "default", "kube-system", "local-path-storage" })
            vm.Namespaces.Add(ns);
        vm.SelectedNamespace = All;

        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => b.Name == "NamespacePicker");

        return (window, picker, vm);
    }

    /// <summary>The framed strip the picker sits in — what the command bar actually reserves for it.</summary>
    private static Border Frame(AutoCompleteBox picker) =>
        picker.GetVisualAncestors().OfType<Border>().First(b => b.Height == 34);

    [Fact]
    public Task The_picker_keeps_its_width_whichever_namespace_is_selected() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, picker, vm) = Build();
                var frame = Frame(picker);

                var atShortest = frame.Bounds.Width;

                vm.SelectedNamespace = "local-path-storage";
                Dispatcher.UIThread.RunJobs();
                window.Measure(window.ClientSize);
                window.Arrange(new Rect(window.ClientSize));

                Assert.Equal(atShortest, frame.Bounds.Width);
                Assert.Equal("local-path-storage", picker.Text);
            },
            CancellationToken.None);

    [Fact]
    public Task It_wears_the_flat_input_the_search_box_wears() =>
        headless.Session.Dispatch(
            () =>
            {
                // The frame around it is the Border's. A text box painting its own inside that one is
                // two frames, and the style that prevents it has to reach through two templates to get
                // there — hence a test, rather than trusting the selector.
                var (_, picker, _) = Build();

                var text = picker.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PART_TextBox");

                Assert.Equal(Brushes.Transparent, text.Background);
                Assert.Equal(default, text.BorderThickness);

                var element = text.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");

                Assert.Equal(Brushes.Transparent, element.Background);
                Assert.Equal(default, element.BorderThickness);
            },
            CancellationToken.None);

    [Fact]
    public Task Pressing_it_offers_every_namespace_with_All_namespaces_first() =>
        headless.Session.Dispatch(
            () =>
            {
                // The ComboBox this replaced showed the whole list on a click. Filtering on the name
                // already selected would show that one name and nothing else.
                var (_, picker, _) = Build();

                Click(picker);

                Assert.True(picker.IsDropDownOpen);
                Assert.Equal(
                    new[] { All, "default", "kube-system", "local-path-storage" },
                    Offered(picker));
            },
            CancellationToken.None);

    [Fact]
    public Task Typing_filters_on_any_part_of_the_name() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, picker, _) = Build();
                picker.Focus();
                Dispatcher.UIThread.RunJobs();

                // "path" sits in the middle of local-path-storage: StartsWith would find nothing.
                Type(window, "path");

                Assert.Equal(["local-path-storage"], Offered(picker));
            },
            CancellationToken.None);

    [Fact]
    public Task Typing_does_not_change_the_namespace_until_something_is_picked() =>
        headless.Session.Dispatch(
            () =>
            {
                // Every keystroke clears the control's own selection. Bound two-way that would be a
                // null namespace — and a cluster reload — per key.
                var (window, picker, vm) = Build();
                picker.Focus();
                Dispatcher.UIThread.RunJobs();

                Type(window, "kube");

                Assert.Equal(All, vm.SelectedNamespace);

                Pick(window);

                Assert.Equal("kube-system", vm.SelectedNamespace);
            },
            CancellationToken.None);

    [Fact]
    public Task Clicking_an_entry_picks_it_and_the_window_lives() =>
        headless.Session.Dispatch(
            () =>
            {
                // The mouse takes a different road out of the drop-down than the keyboard does, and it
                // used to take the window with it: clicking an entry hands focus back to the field
                // while that same click is closing the popup, and opening the list from there reopened
                // a popup halfway torn down. Avalonia then walked off the end of a child list it was
                // detaching. A pick made with the keyboard lands after the close and never showed it.
                var (_, picker, vm) = Build();

                Click(picker);

                Click(Entry(picker, "kube-system"));

                Assert.Equal("kube-system", vm.SelectedNamespace);
                Assert.False(picker.IsDropDownOpen);
            },
            CancellationToken.None);

    [Fact]
    public Task Half_typed_text_is_dropped_when_the_picker_loses_focus() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, picker, vm) = Build();
                vm.SelectedNamespace = "kube-system";
                Dispatcher.UIThread.RunJobs();

                picker.Focus();
                Dispatcher.UIThread.RunJobs();
                Type(window, "zzz");

                // Away, to anything that takes focus.
                window.GetVisualDescendants().OfType<Button>().First(b => b.Focusable).Focus();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("kube-system", picker.Text);
                Assert.Equal("kube-system", vm.SelectedNamespace);
            },
            CancellationToken.None);

    private static void Type(Window window, string text)
    {
        foreach (var c in text)
            window.KeyTextInput(c.ToString());

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Down then Enter — the keyboard way into the list the box is offering.</summary>
    private static void Pick(Window window)
    {
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The open list itself — it hangs in the popup, beside the picker's tree rather than
    /// under it.</summary>
    private static ListBox? List(AutoCompleteBox picker) =>
        (picker.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child as Visual)?
            .GetVisualDescendants().OfType<ListBox>().FirstOrDefault();

    /// <summary>What the open list is holding out.</summary>
    private static string[] Offered(AutoCompleteBox picker) =>
        List(picker)?.ItemsSource?.Cast<string>().ToArray() ?? [];

    /// <summary>One row of the open list, by the name on it.</summary>
    private static Control Entry(AutoCompleteBox picker, string name) =>
        List(picker)!.GetRealizedContainers()!.First(c => Equals(c.DataContext, name));

    /// <summary>Press and release, which is where the list commits a pick.</summary>
    private static void Click(Control entry)
    {
        var pointer = new Pointer(1, PointerType.Mouse, true);

        entry.RaiseEvent(new PointerPressedEventArgs(
            entry, pointer, entry, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        entry.RaiseEvent(new PointerReleasedEventArgs(
            entry, pointer, entry, default, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        Dispatcher.UIThread.RunJobs();
    }
}
