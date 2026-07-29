using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kontena.App.Behaviors;

/// <summary>
/// Keeps a log list on its last line (KON-165).
/// <para>
/// This replaces five near-identical copies of the same twenty lines of code-behind, which had drifted
/// into five different behaviours: none of them scrolled when the view opened, none of them answered a
/// <c>Reset</c>, and only two noticed the Follow button being switched back on. Those differences are
/// what you get when something is written by hand five times.
/// </para>
/// <para>
/// Attached to the <see cref="ListBox"/> rather than written per view, so the next log surface gets the
/// behaviour by adding one attribute — the compose-up console had already been added without it.
/// </para>
/// </summary>
public static class AutoScroll
{
    /// <summary>
    /// Whether the list stays on its last line. Two-way: scrolling up switches it off, scrolling back
    /// to the bottom switches it on, and a view model bound to it keeps its Follow button in step.
    /// <para>
    /// Defaults to true, so a console with no Follow button of its own — the build and compose-up
    /// consoles — still tails without needing one.
    /// </para>
    /// </summary>
    public static readonly AttachedProperty<bool> FollowProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "Follow", typeof(AutoScroll), defaultValue: true,
            defaultBindingMode: BindingMode.TwoWay);

    public static bool GetFollow(ListBox listBox) => listBox.GetValue(FollowProperty);

    public static void SetFollow(ListBox listBox, bool value) => listBox.SetValue(FollowProperty, value);

    /// <summary>
    /// Turns the behaviour on. Separate from <see cref="FollowProperty"/> on purpose: Follow defaults to
    /// true, so binding it to a view model that also starts true is not a change and would never reach a
    /// property-changed handler — the list would sit there with nothing hooked up. This one goes from
    /// false to true exactly once per list, which is a change every time.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("Enabled", typeof(AutoScroll));

    public static bool GetEnabled(ListBox listBox) => listBox.GetValue(EnabledProperty);

    public static void SetEnabled(ListBox listBox, bool value) => listBox.SetValue(EnabledProperty, value);

    private static readonly ConditionalWeakTable<ListBox, Tail> Tails = [];

    static AutoScroll()
    {
        EnabledProperty.Changed.AddClassHandler<ListBox>(OnEnabledChanged);
        FollowProperty.Changed.AddClassHandler<ListBox>(OnFollowChanged);
    }

    private static void OnEnabledChanged(ListBox listBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
            Tails.GetValue(listBox, static box => new Tail(box));
    }

    private static void OnFollowChanged(ListBox listBox, AvaloniaPropertyChangedEventArgs e)
    {
        // Turning Follow back on jumps to the tail straight away. Waiting for the next line means the
        // button does nothing at all on a finished build or a stopped stack — no more lines are coming.
        if (e.GetNewValue<bool>() && Tails.TryGetValue(listBox, out var tail))
            tail.ScrollToEnd();
    }

    /// <summary>
    /// The per-list state: what it is listening to, and whether the scroll under way is its own.
    /// </summary>
    private sealed class Tail
    {
        private readonly ListBox _listBox;
        private INotifyCollectionChanged? _items;
        private ScrollViewer? _scroll;

        /// <summary>
        /// Set while our own scroll is in flight. Without it the tail-follow reads as the user having
        /// scrolled, and the first line after enabling Follow would switch Follow off again.
        /// </summary>
        private bool _scrolling;

        /// <summary>Whether the list had a viewport to scroll in, last time it was laid out.</summary>
        private bool _hadRoom;

        public Tail(ListBox listBox)
        {
            _listBox = listBox;

            HookItems();
            listBox.PropertyChanged += OnListBoxPropertyChanged;

            // The lines are often already there before the view gets its DataContext — history that
            // arrives in one go, or a page rebuilt on the way back to it. Those Adds happened with
            // nobody listening, which is exactly the case where every one of these views used to open
            // at the top.
            listBox.AttachedToVisualTree += (_, _) =>
            {
                HookScrollViewer();
                ScrollToEnd();
            };

        }

        private void OnListBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Attached is not the same moment as laid out (KON-198). Behind a tab — a panel toggled
            // with IsVisible rather than content built on demand — this list is attached from the
            // start and has no size until the tab is picked, which is long after the lines arrived.
            // Its own bounds going from nothing to something is that moment; the effective viewport
            // is not, because a hidden control still reports its parent's.
            if (e.Property == Visual.BoundsProperty)
            {
                var hasRoom = _listBox.Bounds is { Width: > 0, Height: > 0 };

                if (LogTail.ShouldTailOnAppearing(_hadRoom, hasRoom, GetFollow(_listBox), Count))
                {
                    HookScrollViewer();
                    ScrollToEnd();
                }

                _hadRoom = hasRoom;
                return;
            }

            if (e.Property != ItemsControl.ItemsSourceProperty)
                return;

            // A different list entirely — another container's logs, another pod's. Follow it, and land
            // on its end rather than on the offset the previous one happened to have.
            HookItems();
            ScrollToEnd();
        }

        private void HookItems()
        {
            if (_items is not null)
                _items.CollectionChanged -= OnItemsChanged;

            _items = _listBox.ItemsSource as INotifyCollectionChanged;

            if (_items is not null)
                _items.CollectionChanged += OnItemsChanged;
        }

        private void HookScrollViewer()
        {
            if (_scroll is not null)
                return;

            _scroll = _listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is null)
                return;

            _scroll.PropertyChanged += OnScrollPropertyChanged;
        }

        private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ScrollViewer.OffsetProperty || _scroll is null)
                return;

            var follow = LogTail.FollowAfterScroll(
                GetFollow(_listBox), _scrolling,
                _scroll.Offset.Y, _scroll.Extent.Height, _scroll.Viewport.Height);

            if (follow != GetFollow(_listBox))
                SetFollow(_listBox, follow);
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (LogTail.ShouldTail(e.Action, GetFollow(_listBox), Count))
                ScrollToEnd();
        }

        private int Count => (_listBox.ItemsSource as System.Collections.ICollection)?.Count ?? 0;

        /// <summary>
        /// Move to the last line, after the new one has been realised and laid out. Scrolling
        /// synchronously lands on the extent the list had a moment ago, which is one line short.
        /// </summary>
        public void ScrollToEnd()
        {
            if (!GetFollow(_listBox))
                return;

            _scrolling = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        var count = Count;
                        if (count > 0)
                            _listBox.ScrollIntoView(count - 1);
                    }
                    finally
                    {
                        // Released on a later turn than the scroll itself: the offset change the scroll
                        // causes arrives after this callback, and clearing the flag here would let it
                        // through as a user scroll.
                        Dispatcher.UIThread.Post(() => _scrolling = false, DispatcherPriority.Background);
                    }
                },
                DispatcherPriority.Background);
        }
    }
}
