using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

public sealed partial class NetworkRowViewModel : ObservableObject
{
    private readonly NetworkSummary _n;
    private readonly NetworksViewModel _parent;

    public NetworkRowViewModel(NetworkSummary network, NetworksViewModel parent)
    {
        _n = network;
        _parent = parent;
    }

    public string Id => _n.Id;
    public string Name => _n.Name;
    public string Kind => _n.IsBuiltIn ? "built-in" : "custom";
    public string Driver => _n.Driver;
    public string Scope => _n.Scope;
    public string Subnet => string.IsNullOrEmpty(_n.Subnet) ? "—" : _n.Subnet;
    public bool IsBuiltIn => _n.IsBuiltIn;
    public bool CanDelete => !_n.IsBuiltIn;

    public string AttachedText => _n.AttachedContainers.Count > 0
        ? $"{_n.AttachedContainers.Count} container{(_n.AttachedContainers.Count == 1 ? "" : "s")}"
        : "— none";

    public IBrush DriverBrush => new SolidColorBrush(Color.Parse(_n.Driver switch
    {
        "bridge" => "#5AB8FF",
        "host" => "#F5B14C",
        "overlay" => "#7C6BF5",
        _ => "#5C6675",
    }));

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task Delete() => _parent.DeleteAsync(Id);
}
