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
        TableGrid.Children.Clear();
        TableGrid.RowDefinitions.Clear();
        TableGrid.ColumnDefinitions.Clear();

        if (_vm?.Table is not { Columns.Count: > 0 } table)
            return;

        // Only what kubectl would print. The wide columns are still in the table; showing them all
        // makes every listing scroll sideways for information nobody asked for.
        var shown = table.Columns
            .Select((column, index) => (column, index))
            .Where(c => c.column.Priority == 0)
            .ToArray();

        if (shown.Length == 0)
            shown = [.. table.Columns.Select((column, index) => (column, index))];

        var columns = shown.Select(s => s.column).ToArray();
        var indexes = shown.Select(s => s.index).ToArray();
        var rows = _vm.Rows;

        foreach (var _ in columns)
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // One more for the row actions.
        TableGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        TableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var _ in rows)
            TableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var c = 0; c < columns.Length; c++)
            TableGrid.Children.Add(Header(columns[c].Name, c));

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Length; c++)
            {
                var index = indexes[c];
                var text = index >= 0 && index < rows[r].Cells.Count ? rows[r].Cells[index] : string.Empty;

                TableGrid.Children.Add(Cell(text, c, r + 1, mono: c == 0));
            }

            TableGrid.Children.Add(Actions(rows[r], columns.Length, r + 1));
        }
    }

    /// <summary>
    /// The same clickable header the cluster lists have had since KON-318, rather than a second kind
    /// of column header that happens to look alike. Its state is set rather than bound because the
    /// whole grid is rebuilt whenever the sort changes.
    /// </summary>
    private SortableHeader Header(string name, int column)
    {
        var header = new SortableHeader
        {
            Text = name.ToUpperInvariant(),
            Key = name,
            SortColumn = _vm?.SortColumn,
            SortDescending = _vm?.SortDescending ?? false,
            SortCommand = _vm?.SortByCommand,
            Margin = new Thickness(0, 0, 22, 8),
        };

        Grid.SetColumn(header, column);
        Grid.SetRow(header, 0);
        return header;
    }

    private static TextBlock Cell(string text, int column, int row, bool mono)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Margin = new Thickness(0, 3, 22, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension(mono ? "Text" : "TextDim"),
        };

        if (mono)
            block.FontFamily = new FontFamily("JetBrains Mono, monospace");

        Grid.SetColumn(block, column);
        Grid.SetRow(block, row);
        return block;
    }

    private StackPanel Actions(ResourceRow row, int column, int gridRow)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var yaml = new Button { Content = "YAML", Classes = { "ghost" } };
        yaml.Click += (_, _) => _ = _vm?.ShowManifestAsync(row);
        panel.Children.Add(yaml);

        // Only where the API server says the verb exists: a delete button that could only ever fail is
        // worse than no button (KON-117).
        if (_vm?.CanDeleteSelected == true)
        {
            var delete = new Button { Content = "Delete", Classes = { "ghost" } };
            delete.Click += (_, _) => _vm?.ConfirmDelete(row);
            panel.Children.Add(delete);
        }

        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, gridRow);
        return panel;
    }
}
