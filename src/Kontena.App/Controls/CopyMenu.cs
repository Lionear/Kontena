using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Kontena.App.Controls;

/// <summary>
/// A table row's context menu for copying its name (KON-482). Set <c>CopyMenu.Name</c> on the row,
/// and <c>CopyMenu.Namespace</c> too for a namespaced kind, which adds "Copy namespace/name".
/// <para>
/// Attached rather than a row control or a style: the rows of the tables share no template, and a
/// style can only hand out one shared menu. The copy is the same clipboard move as
/// <see cref="CopyButton"/> (KON-481).
/// </para>
/// </summary>
public static class CopyMenu
{
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Name", typeof(CopyMenu));

    public static readonly AttachedProperty<string?> NamespaceProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Namespace", typeof(CopyMenu));

    static CopyMenu() => NameProperty.Changed.AddClassHandler<Control>((row, _) => Attach(row));

    public static string? GetName(Control row) => row.GetValue(NameProperty);
    public static void SetName(Control row, string? value) => row.SetValue(NameProperty, value);
    public static string? GetNamespace(Control row) => row.GetValue(NamespaceProperty);
    public static void SetNamespace(Control row, string? value) => row.SetValue(NamespaceProperty, value);

    private static void Attach(Control row)
    {
        if (row.ContextMenu is not null)
            return;

        var menu = new ContextMenu();
        Fill(menu, row);
        // Read at opening, not at attach: a row's bindings can still be settling when the name arrives.
        menu.Opening += (_, _) => Fill(menu, row);
        row.ContextMenu = menu;

        // The keyboard route (KON-56). Avalonia opens a context menu on a right-click only, so the
        // Menu key and Shift+F10 are caught here, bubbling up from the row's focused name.
        row.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            {
                menu.Open(row);
                e.Handled = true;
            }
        });
    }

    private static void Fill(ContextMenu menu, Control row)
    {
        var name = GetName(row);
        var ns = GetNamespace(row);

        menu.Items.Clear();
        menu.Items.Add(Item("Copy name", name, row));
        if (!string.IsNullOrEmpty(ns))
            menu.Items.Add(Item("Copy namespace/name", $"{ns}/{name}", row));
    }

    private static MenuItem Item(string header, string? text, Control row)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(row)?.Clipboard is { } clipboard)
                await clipboard.SetValueAsync(DataFormat.Text, text);
        };
        return item;
    }
}
