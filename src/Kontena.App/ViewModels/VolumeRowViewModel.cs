using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

public sealed partial class VolumeRowViewModel : ObservableObject
{
    private readonly VolumeSummary _v;
    private readonly VolumesViewModel _parent;

    public VolumeRowViewModel(VolumeSummary volume, VolumesViewModel parent)
    {
        _v = volume;
        _parent = parent;
    }

    public string Name => _v.Name;
    public string Driver => _v.Driver;
    public string Mountpoint => _v.Mountpoint;
    public string SizeText => Format.Size(_v.SizeBytes);
    public bool IsDangling => _v.IsDangling;

    public string UsedByText => _v.UsedBy.Count > 0
        ? string.Join(", ", _v.UsedBy)
        : "— not mounted";

    [RelayCommand]
    private Task Delete() => _parent.DeleteAsync(Name);
}
