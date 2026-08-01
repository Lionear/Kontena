using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Kontena.App.Controls;

/// <summary>
/// A clickable column header for the cluster resource lists (KON-318): the same faint label every
/// list already drew, now a button that asks the page to sort by it, with an arrow that says which
/// column and direction currently apply.
/// </summary>
public sealed partial class SortableHeader : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SortableHeader, string>(nameof(Text), string.Empty);

    /// <summary>The key this header sorts by — matches an entry in the page's SortColumns.</summary>
    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<SortableHeader, string>(nameof(Key), string.Empty);

    /// <summary>The page's current sort column, so this header knows whether it is the active one.</summary>
    public static readonly StyledProperty<string?> SortColumnProperty =
        AvaloniaProperty.Register<SortableHeader, string?>(nameof(SortColumn));

    public static readonly StyledProperty<bool> SortDescendingProperty =
        AvaloniaProperty.Register<SortableHeader, bool>(nameof(SortDescending));

    public static readonly StyledProperty<ICommand?> SortCommandProperty =
        AvaloniaProperty.Register<SortableHeader, ICommand?>(nameof(SortCommand));

    /// <summary>Derived from the three properties above — not set directly.</summary>
    public static readonly StyledProperty<string> ArrowProperty =
        AvaloniaProperty.Register<SortableHeader, string>(nameof(Arrow), string.Empty);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public string? SortColumn
    {
        get => GetValue(SortColumnProperty);
        set => SetValue(SortColumnProperty, value);
    }

    public bool SortDescending
    {
        get => GetValue(SortDescendingProperty);
        set => SetValue(SortDescendingProperty, value);
    }

    public ICommand? SortCommand
    {
        get => GetValue(SortCommandProperty);
        set => SetValue(SortCommandProperty, value);
    }

    public string Arrow => GetValue(ArrowProperty);

    public SortableHeader()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SortColumnProperty || change.Property == SortDescendingProperty
            || change.Property == KeyProperty)
        {
            SetValue(ArrowProperty, SortColumn == Key ? (SortDescending ? "▼" : "▲") : string.Empty);
        }
    }
}
