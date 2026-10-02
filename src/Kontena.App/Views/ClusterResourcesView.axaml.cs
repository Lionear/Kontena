using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Kontena.App.Controls;
using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Views;

/// <summary>
/// Draws the listing whose shape only the cluster knows (KON-75).
/// <para>
/// The grid is built here rather than templated in XAML because the number of columns is the API
/// server's answer, and a template has to be written against a known one. Building it means the columns
/// line up as a real table instead of a row of independently sized stacks.
/// </para>
/// </summary>
public partial class ClusterResourcesView : UserControl
{
    private ClusterResourcesViewModel? _vm;

    public ClusterResourcesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as ClusterResourcesViewModel;

        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rows, not Table: the listing on screen is the filtered and sorted one, and it changes on
        // every keystroke and every header click without the table underneath it moving.
        if (e.PropertyName == nameof(ClusterResourcesViewModel.Rows))
            Rebuild();
    }

    private void OnKindClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is Button { DataContext: ApiResourceItem item })
            _vm.Selected = item;
    }

    private void Rebuild()
    {
        TableRows.Children.Clear();

        if (_vm?.Table is not { Columns.Count: > 0 } table || _vm.Rows.Count == 0)
        {
            TableBorder.IsVisible = false;
            return;
        }

        TableBorder.IsVisible = true;

        // Only what kubectl would print. The wide columns are still in the table; showing them all
        // makes every listing scroll sideways for information nobody asked for.
        var shown = table.Columns
            .Select((column, index) => (column, index))
            .Where(c => c.column.Priority == 0)
            .ToArray();

        if (shown.Length == 0)
            shown = [.. table.Columns.Select((column, index) => (column, index))];

        // The namespace beside the name, as on Pods: the server's Table leaves it out, because kubectl
        // adds that column itself when it lists across namespaces.
        var namespaced = _vm.Rows.Any(r => r.Reference.Namespace is { Length: > 0 });

        TableRows.Children.Add(HeaderRow(shown.Select(s => s.column.Name).ToArray(), namespaced));

        foreach (var row in _vm.Rows)
            TableRows.Children.Add(Row(row, shown.Select(s => s.index).ToArray(), namespaced));
    }

    /// <summary>
    /// Proportional columns, the way the typed lists size theirs: the name widest, the namespace next,
    /// the rest even, and a fixed slot for two pills (KON-332's 140). Every row builds the same
    /// definitions, so the columns line up without a shared-size scope.
    /// </summary>
    private static Grid Columns(int cells, bool namespaced)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1.8, GridUnitType.Star));

        if (namespaced)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1.1, GridUnitType.Star));

        for (var i = 1; i < cells; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        grid.ColumnDefinitions.Add(new ColumnDefinition(140, GridUnitType.Pixel));
        return grid;
    }

    private Border HeaderRow(string[] names, bool namespaced)
    {
        var grid = Columns(names.Length, namespaced);
        var column = 0;

        grid.Children.Add(At(Header(names[0]), column++));

        if (namespaced)
            grid.Children.Add(At(new TextBlock { Text = "NAMESPACE", Classes = { "colhead" } }, column++));

        foreach (var name in names.Skip(1))
            grid.Children.Add(At(Header(name), column++));

        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 10),
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceRaised"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("Border"),
        };
    }

    private Border Row(ResourceRow row, int[] indexes, bool namespaced)
    {
        var grid = Columns(indexes.Length, namespaced);
        var column = 0;

        grid.Children.Add(At(NameCell(row, CellText(row, indexes[0])), column++));

        if (namespaced)
            grid.Children.Add(At(Cell(row.Reference.Namespace ?? string.Empty, "TextDim", mono: false), column++));

        foreach (var index in indexes.Skip(1))
            grid.Children.Add(At(Cell(CellText(row, index), "TextDim", mono: true), column++));

        grid.Children.Add(At(Actions(row), column));

        return new Border
        {
            Child = grid,
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.PaddingProperty] = new DynamicResourceExtension("RowPadding"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderSoft"),
        };
    }

    private static string CellText(ResourceRow row, int index) =>
        index >= 0 && index < row.Cells.Count ? row.Cells[index] : string.Empty;

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    /// <summary>
    /// The same clickable header the cluster lists have had since KON-318, rather than a second kind
    /// of column header that happens to look alike. Its state is set rather than bound because the
    /// whole grid is rebuilt whenever the sort changes.
    /// </summary>
    private SortableHeader Header(string name) => new()
    {
        Text = name.ToUpperInvariant(),
        Key = name,
        SortColumn = _vm?.SortColumn,
        SortDescending = _vm?.SortDescending ?? false,
        SortCommand = _vm?.SortByCommand,
    };

    private static TextBlock Cell(string text, string brush, bool mono)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brush),
            [ToolTip.TipProperty] = text,
        };

        if (mono)
            block.FontFamily = new FontFamily("JetBrains Mono, monospace");

        return block;
    }

    /// <summary>
    /// The name opens the object's detail page (KON-483), the way it does on every other list in the
    /// app.
    /// </summary>
    private Button NameCell(ResourceRow row, string text)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
            Classes = { "link" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => _vm?.OpenDetail(row);
        return button;
    }

    private StackPanel Actions(ResourceRow row)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var yaml = new Button { Content = "YAML", Classes = { "pill" } };
        yaml.Click += (_, _) => _vm?.OpenDetail(row, "yaml");
        panel.Children.Add(yaml);

        // Only where the API server says the verb exists: a delete button that could only ever fail is
        // worse than no button (KON-117).
        if (_vm?.CanDeleteSelected == true)
        {
            var delete = new Button { Content = "Delete", Classes = { "pill" } };
            delete.Click += (_, _) => _vm?.ConfirmDelete(row);
            panel.Children.Add(delete);
        }

        return panel;
    }
}
