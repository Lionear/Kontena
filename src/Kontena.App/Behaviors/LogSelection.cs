using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;

namespace Kontena.App.Behaviors;

/// <summary>
/// Selecting several log lines at once and copying them together (KON-463).
/// <para>
/// KON-452 made a line's text selectable by drawing it with a <see cref="SelectableTextBlock"/>, which
/// only reaches as far as one line: a drag selects inside the line it started on, so a stack trace came
/// out one line at a time. Rows are the other half of that.
/// </para>
/// <para>
/// <b>Why this is not just <c>SelectionMode="Multiple"</c>.</b> KON-452 also gave every
/// <c>SelectableTextBlock</c> a transparent background so the pointer can reach the text — which means
/// the press now stops at the text and never reaches the <c>ListBoxItem</c> behind it. Turning the list
/// to Multiple on its own therefore selects nothing at all, whatever you click: verified, not assumed.
/// The two have to be told apart deliberately, and the modifier is what does it — a plain press is a
/// text selection, Ctrl or Shift is a row selection. Neither gesture takes anything from the other.
/// </para>
/// <para>
/// Attached to the <see cref="ListBox"/> in the manner of <see cref="AutoScroll"/>, because all three
/// log viewers need exactly this and a fourth would otherwise get its own near-copy.
/// </para>
/// </summary>
public static class LogSelection
{
    /// <summary>Turns the behaviour on. False to true once per list, as in <see cref="AutoScroll"/>.</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("Enabled", typeof(LogSelection));

    public static bool GetEnabled(ListBox listBox) => listBox.GetValue(EnabledProperty);

    public static void SetEnabled(ListBox listBox, bool value) => listBox.SetValue(EnabledProperty, value);

    private static readonly ConditionalWeakTable<ListBox, Rows> Attached = [];

    static LogSelection()
    {
        EnabledProperty.Changed.AddClassHandler<ListBox>(
            (listBox, e) =>
            {
                if (e.GetNewValue<bool>())
                    Attached.GetValue(listBox, static box => new Rows(box));
            });
    }

    /// <summary>The per-list state: which row a range grows from, and what was last right-clicked.</summary>
    private sealed class Rows
    {
        private readonly ListBox _listBox;

        /// <summary>Where a Shift-click measures from — the last row picked without Shift.</summary>
        private int _anchor = -1;

        /// <summary>The line the context menu was opened on, for the single-line half of Copy.</summary>
        private SelectableTextBlock? _rightClicked;

        public Rows(ListBox listBox)
        {
            _listBox = listBox;

            // One menu on the list, doing both jobs: the selected rows if there are any, otherwise the
            // text selection inside the line that was right-clicked. Nothing KON-452 offered is lost.
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) => _ = CopyAsync();
            listBox.ContextMenu = new ContextMenu { ItemsSource = new[] { copy } };

            // Tunnelled, both of them, for the same reason AutoScroll tunnels: the line under the
            // pointer handles its own press and its own Ctrl-C, so a bubbling handler on the list would
            // hear about neither.
            listBox.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            listBox.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
            listBox.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(_listBox).Properties;

            if (point.IsRightButtonPressed)
            {
                OpenMenu(e);
                return;
            }

            if (!point.IsLeftButtonPressed)
                return;

            if (RowUnder(e.Source) is not { } index)
                return;

            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (!ctrl && !shift)
            {
                // A bare press is a text selection (KON-452) and is left entirely alone — but it does
                // drop the rows, so Copy never quietly hands back a selection the user has moved on
                // from, and it moves the anchor, so click-then-Shift-click reads as one range.
                _listBox.Selection.Clear();
                _anchor = index;
                return;
            }

            if (shift && _anchor >= 0)
            {
                _listBox.Selection.Clear();
                _listBox.Selection.SelectRange(_anchor, index);
            }
            else if (_listBox.Selection.IsSelected(index))
            {
                _listBox.Selection.Deselect(index);
            }
            else
            {
                _listBox.Selection.Select(index);
                _anchor = index;
            }

            // The row takes the focus, not the list: the list itself is not focusable, so focusing it
            // leaves the window with no focused element at all and Ctrl-C below never reaches this
            // handler — the key event only tunnels through what is on the focused element's path.
            _listBox.ContainerFromIndex(index)?.Focus();

            // The press stops here: starting a text selection on the same click would leave the line
            // half-highlighted under a row that is also selected, and neither would be what was meant.
            e.Handled = true;
        }

        /// <summary>
        /// The right-click menu, opened here rather than left to the framework.
        /// <para>
        /// Fluent gives every <see cref="SelectableTextBlock"/> its own Copy/Cut/Paste flyout, and the
        /// line is what the pointer reaches (KON-452) — so the flyout on the line answers the
        /// right-click and the menu on the list never opens, exactly where the rows are. Taking the
        /// flyout off with a style does not work: a control theme's setter holds against it, which the
        /// test for this pins down. Handling the press is what settles it — the line never sees the
        /// button, so it never asks for its own menu, and the list opens the one menu that knows about
        /// both the rows and the text.
        /// </para>
        /// </summary>
        private void OpenMenu(PointerPressedEventArgs e)
        {
            _rightClicked = e.Source as SelectableTextBlock;
            _listBox.ContextMenu?.Open(_listBox);
            e.Handled = true;
        }

        /// <summary>
        /// The other half of <see cref="OpenMenu"/>: the framework asks for a context menu on the
        /// release rather than the press, and it asks the line — which would put Fluent's flyout up on
        /// top of the menu the press already opened, both showing at once. Swallowing the release is
        /// what stops the line ever asking.
        /// </summary>
        private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
                e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.C)
                return;

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                return;

            // A text selection inside a line wins: that is KON-452's Ctrl-C and it is what the user is
            // looking at. Without rows selected there is nothing here to answer with anyway.
            if (e.Source is SelectableTextBlock { SelectedText.Length: > 0 })
                return;

            if (_listBox.Selection.Count == 0)
                return;

            _ = CopyAsync();
            e.Handled = true;
        }

        /// <summary>The index of the row the event came from, or null if it came from beside the rows.</summary>
        private int? RowUnder(object? source) =>
            (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is { } row
            && _listBox.IndexFromContainer(row) is >= 0 and var index
                ? index
                : null;

        private async Task CopyAsync()
        {
            if (TopLevel.GetTopLevel(_listBox)?.Clipboard is not { } clipboard)
                return;

            var text = _listBox.Selection.Count > 0
                // By index, not by the order they were clicked: a Ctrl-click picking up a line above one
                // already selected still pastes in the order it is read on screen.
                ? string.Join(
                    '\n',
                    _listBox.Selection.SelectedIndexes.Order()
                        .Select(i => (_listBox.Items[i] as ILogLine)?.ForClipboard)
                        .OfType<string>())
                : _rightClicked?.SelectedText ?? string.Empty;

            if (text.Length > 0)
                await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }
}
