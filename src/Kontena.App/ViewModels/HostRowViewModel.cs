using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// One machine in the host table (KON-233). Edited in place, like the port rows on the local create
/// form — a row of fields is the edit, so there is no second screen to open and nothing to save.
/// </summary>
public sealed partial class HostRowViewModel(Action<HostRowViewModel> remove) : ObservableObject
{
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _nodeName = string.Empty;
    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string _keyPath = string.Empty;
    [ObservableProperty] private ClusterHostRole _role = ClusterHostRole.Worker;

    /// <summary>Both roles, for the row's dropdown. Two values, so the list is the whole choice.</summary>
    public IReadOnlyList<ClusterHostRole> Roles { get; } =
        [ClusterHostRole.Controller, ClusterHostRole.Worker];

    /// <summary>The host, or null while the row has no address to be about.</summary>
    public RemoteClusterHost? Host =>
        string.IsNullOrWhiteSpace(Address)
            ? null
            : new RemoteClusterHost(Address.Trim(), Role)
            {
                NodeName = Blank(NodeName),
                User = Blank(User),
                KeyPath = Blank(KeyPath),
            };

    /// <summary>An untouched row — ignored rather than complained about, as an empty port row is.</summary>
    public bool IsEmpty =>
        Address.Length == 0 && NodeName.Length == 0 && User.Length == 0 && KeyPath.Length == 0;

    [RelayCommand]
    private void Remove() => remove(this);

    /// <summary>Raised on every edit, so the table can redo its counts and its validation.</summary>
    public event EventHandler? Edited;

    partial void OnAddressChanged(string value) => Changed();
    partial void OnNodeNameChanged(string value) => Changed();
    partial void OnUserChanged(string value) => Changed();
    partial void OnKeyPathChanged(string value) => Changed();
    partial void OnRoleChanged(ClusterHostRole value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(IsEmpty));
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
