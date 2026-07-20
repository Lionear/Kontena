using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ContainerDetailView : UserControl
{
    private INotifyCollectionChanged? _lines;
    private ContainerDetailViewModel? _vm;

    public ContainerDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_lines is not null)
            _lines.CollectionChanged -= OnLinesChanged;
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as ContainerDetailViewModel;
        _lines = _vm?.Lines;

        if (_lines is not null)
            _lines.CollectionChanged += OnLinesChanged;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-enabling Follow jumps straight to the tail.
        if (e.PropertyName == nameof(ContainerDetailViewModel.AutoScroll) && _vm?.AutoScroll == true)
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (DataContext is not ContainerDetailViewModel vm || !vm.AutoScroll)
            return;

        // Defer until after the new item has been realized and laid out —
        // scrolling synchronously here lands on the previous extent.
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (DataContext is not ContainerDetailViewModel vm || !vm.AutoScroll)
            return;

        var count = vm.Lines.Count;
        if (count == 0)
            return;

        this.FindControl<ListBox>("LogList")?.ScrollIntoView(count - 1);
    }
}
