using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>The "Scale workload" modal: set the desired replica count and apply it via the OAL.</summary>
public partial class ScaleWorkloadViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly Workload _workload;
    private readonly Action _onClose;
    private readonly Func<Task> _onDone;

    public ScaleWorkloadViewModel(IClusterEngine cluster, Workload workload, Action onClose, Func<Task> onDone)
    {
        _cluster = cluster;
        _workload = workload;
        _onClose = onClose;
        _onDone = onDone;

        Name = workload.Name;
        Namespace = workload.Namespace;
        Kind = workload.Kind.ToString();
        CurrentReplicas = workload.Desired;
        _replicas = workload.Desired;
    }

    public string Name { get; }
    public string Namespace { get; }
    public string Kind { get; }
    public int CurrentReplicas { get; }

    [ObservableProperty] private int _replicas;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public bool CanApply => Replicas >= 0 && Replicas != CurrentReplicas && !IsBusy;

    partial void OnReplicasChanged(int value) => OnPropertyChanged(nameof(CanApply));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    [RelayCommand]
    private void Increment() => Replicas++;

    [RelayCommand]
    private void Decrement()
    {
        if (Replicas > 0)
            Replicas--;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            var reference = new ResourceRef(WorkloadKindGvk(_workload.Kind), Namespace, Name);
            Services.Diag.Action($"scale {_workload.Kind}", $"{Namespace}/{Name} to {Replicas}");
            await _cluster.ScaleAsync(reference, Replicas);
            await _onDone();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClose();

    private static GroupVersionKind WorkloadKindGvk(WorkloadKind kind) => kind switch
    {
        WorkloadKind.StatefulSet => new GroupVersionKind("apps", "v1", "StatefulSet"),
        WorkloadKind.ReplicaSet => new GroupVersionKind("apps", "v1", "ReplicaSet"),
        _ => GroupVersionKind.Deployment,
    };
}
