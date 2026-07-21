using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ComposeLogsView : UserControl
{
    private INotifyCollectionChanged? _lines;

    public ComposeLogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_lines is not null)
            _lines.CollectionChanged -= OnLinesChanged;

        _lines = (DataContext as ComposeLogsViewModel)?.Lines;

        if (_lines is not null)
            _lines.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (DataContext is not ComposeLogsViewModel vm || !vm.AutoScroll || vm.Lines.Count == 0)
            return;

        // Tail-follow after layout, only while Follow is on.
        Dispatcher.UIThread.Post(
            () => this.FindControl<ListBox>("LogList")?.ScrollIntoView(vm.Lines.Count - 1),
            DispatcherPriority.Background);
    }
}
