using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ClusterPodDetailView : UserControl
{
    private INotifyCollectionChanged? _lines;
    private ClusterPodDetailViewModel? _vm;

    public ClusterPodDetailView()
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

        _vm = DataContext as ClusterPodDetailViewModel;
        _lines = _vm?.Lines;

        if (_lines is not null)
            _lines.CollectionChanged += OnLinesChanged;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClusterPodDetailViewModel.AutoScroll) && _vm?.AutoScroll == true)
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;
        if (DataContext is not ClusterPodDetailViewModel vm || !vm.AutoScroll)
            return;

        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (DataContext is not ClusterPodDetailViewModel vm || !vm.AutoScroll)
            return;

        var count = vm.Lines.Count;
        if (count == 0)
            return;

        this.FindControl<ListBox>("LogList")?.ScrollIntoView(count - 1);
    }
}
