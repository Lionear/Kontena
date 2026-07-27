using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>Display + interaction wrapper around a <see cref="ContainerSummary"/>.</summary>
public partial class ContainerRowViewModel : ContainerListRowViewModel
{
    private ContainerSummary _c;
    private readonly ContainersViewModel _parent;

    public ContainerRowViewModel(ContainerSummary container, ContainersViewModel parent)
    {
        _c = container;
        _parent = parent;
    }

    /// <summary>Patch this row in place from a fresh summary (same container Id).</summary>
    public void Update(ContainerSummary container)
    {
        _c = container;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(BackendChip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PortsText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ManagedByText));
        OnPropertyChanged(nameof(StatusBrush));

        if (!IsRunning)
        {
            CpuText = "—";
            MemText = "—";
        }
    }

    /// <summary>The underlying summary, handed to the detail page when opened.</summary>
    public ContainerSummary Summary => _c;

    public string Id => _c.Id;
    public string Name => _c.Name;
    public string Image => _c.Image;

    public override string SortKey => Name;

    /// <summary>
    /// The Compose project this belongs to, or null. Read from the label Compose itself writes, so a
    /// stack started from the CLI groups exactly like one started here (KON-159).
    /// </summary>
    public string? Project =>
        _c.Labels.TryGetValue(ComposeProjectsViewModel.ProjectLabel, out var p) && p.Length > 0 ? p : null;

    /// <summary>
    /// The service name inside its project — <c>web</c>, not <c>azuriom-web-1</c>. Shown beside the
    /// container name in a group, because within a stack the service is what people call it.
    /// </summary>
    public string? Service =>
        _c.Labels.TryGetValue(ComposeProjectsViewModel.ServiceLabel, out var s) && s.Length > 0 ? s : null;

    public bool HasService => Service is not null;

    /// <summary>True while this row is drawn inside a group, which indents it and shows the service.</summary>
    [ObservableProperty] private bool _isChild;
    public string Backend => _c.Backend;
    public string BackendChip => _c.Backend.Length > 0 ? _c.Backend[..1].ToUpperInvariant() : "?";

    public bool IsRunning => _c.State == ContainerState.Running;
    public bool IsNotRunning => !IsRunning;

    /// <summary>True when another app (e.g. SQL Explorer) manages this container.</summary>
    public bool IsManaged => _c.IsManagedExternally;
    public string ManagedByText => $"Managed · {Format.ManagedSource(_c.ManagedSource)}";

    public string StatusText => string.IsNullOrWhiteSpace(_c.Status) ? _c.State.ToString() : _c.Status;

    public string PortsText => _c.Ports.Count == 0
        ? "—"
        : string.Join("  ", _c.Ports.Select(p => $"{p.HostPort}→{p.ContainerPort}"));

    /// <summary>Status dot color, derived from the normalized state.</summary>
    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(_c.State switch
    {
        ContainerState.Running => "#34D399",
        ContainerState.Paused or ContainerState.Restarting => "#F5B14C",
        ContainerState.Exited or ContainerState.Dead => "#F87171",
        _ => "#5C6675",
    }));

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memText = "—";

    /// <summary>Apply a live stats sample to the row.</summary>
    public void ApplyStats(ContainerStats s)
    {
        CpuText = $"{s.CpuPercent:0.0}%";
        MemText = $"{s.MemoryUsedBytes / 1_000_000} MB";
    }

    [RelayCommand]
    private void Open() => _parent.OpenDetail(this);

    [RelayCommand]
    private Task Start() => _parent.StartAsync(Id);

    [RelayCommand]
    private Task Stop() => _parent.StopAsync(Id);

    [RelayCommand]
    private Task Restart() => _parent.RestartAsync(Id);

    [RelayCommand]
    private void Remove() => _parent.ConfirmRemove(this);
}
