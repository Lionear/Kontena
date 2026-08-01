using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;

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

    /// <summary>Containers that have this volume mounted; the confirm names them (KON-126).</summary>
    public IReadOnlyList<string> MountedBy => _v.UsedBy;

    /// <summary>
    /// Whether the browse action is offered for this row. Reading a volume needs the engine to mount it
    /// into a container, so it is a capability rather than something every backend can do.
    /// </summary>
    public bool CanBrowse => _parent.CanBrowse;

    [RelayCommand]
    private void Browse() => _parent.RequestBrowseVolume?.Invoke(Name);

    [RelayCommand]
    private void Delete() => _parent.ConfirmDelete(this);
}
