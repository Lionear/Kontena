using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>The Containers page: lists containers from the active engine and drives actions.</summary>
public partial class ContainersViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;

    public ContainersViewModel(IContainerEngine engine) => _engine = engine;

    private readonly List<ContainerRowViewModel> _all = [];

    /// <summary>Filtered view bound to the UI.</summary>
    public ObservableCollection<ContainerRowViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var row in _all.Where(Matches))
            Items.Add(row);
    }

    private bool Matches(ContainerRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.Image.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _stoppedCount;

    [ObservableProperty]
    private string _cpuTotalText = "0%";

    [ObservableProperty]
    private string _memTotalText = "0 MB";

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _engine.ListContainersAsync();
            _all.Clear();
            foreach (var c in list)
                _all.Add(new ContainerRowViewModel(c, this));

            RunningCount = list.Count(c => c.State == ContainerState.Running);
            StoppedCount = list.Count - RunningCount;

            double cpu = 0;
            long mem = 0;
            foreach (var row in _all.Where(r => r.IsRunning))
            {
                try
                {
                    using var statsCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await foreach (var s in _engine.StreamStatsAsync(row.Id, statsCts.Token))
                    {
                        row.ApplyStats(s);
                        cpu += s.CpuPercent;
                        mem += s.MemoryUsedBytes;
                        break; // one sample is enough for the overview
                    }
                }
                catch
                {
                    // A single container's stats failing must never sink the whole list.
                }
            }

            CpuTotalText = $"{cpu:0}%";
            MemTotalText = $"{mem / 1_000_000} MB";

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StartAsync(string id)
    {
        await _engine.StartContainerAsync(id);
        await LoadAsync();
    }

    public async Task StopAsync(string id)
    {
        await _engine.StopContainerAsync(id);
        await LoadAsync();
    }

    public async Task RestartAsync(string id)
    {
        await _engine.RestartContainerAsync(id);
        await LoadAsync();
    }

    public async Task RemoveAsync(string id)
    {
        await _engine.RemoveContainerAsync(id, force: true);
        await LoadAsync();
    }
}
