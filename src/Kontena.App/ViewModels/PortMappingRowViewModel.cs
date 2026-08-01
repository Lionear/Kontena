using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// One host-port row on the create form (KON-76). Ports are strings here, not numbers: a field being
/// briefly empty while it is typed in is normal, and a spinner that snaps back to 0 fights the user.
/// </summary>
public sealed partial class PortMappingRowViewModel(Action<PortMappingRowViewModel> remove)
    : ObservableObject
{
    [ObservableProperty] private string _hostPort = string.Empty;
    [ObservableProperty] private string _nodePort = string.Empty;
    [ObservableProperty] private string _protocol = "TCP";

    public IReadOnlyList<string> Protocols { get; } = ["TCP", "UDP"];

    /// <summary>The mapping, or null when this row is not (yet) a usable pair.</summary>
    public ClusterPortMapping? Mapping =>
        Port(HostPort) is { } host && Port(NodePort) is { } node
            ? new ClusterPortMapping(host, node, Protocol.ToLowerInvariant())
            : null;

    /// <summary>True when something was typed but it is not a valid pair — worth saying out loud.</summary>
    public bool IsIncomplete => !IsEmpty && Mapping is null;

    /// <summary>An untouched row. Those are ignored rather than complained about.</summary>
    public bool IsEmpty => HostPort.Length == 0 && NodePort.Length == 0;

    [RelayCommand]
    private void Remove() => remove(this);

    partial void OnHostPortChanged(string value) => Changed();
    partial void OnNodePortChanged(string value) => Changed();
    partial void OnProtocolChanged(string value) => Changed();

    /// <summary>Raised on every edit so the form can redo its preview and its validation.</summary>
    public event EventHandler? Edited;

    private void Changed()
    {
        OnPropertyChanged(nameof(Mapping));
        OnPropertyChanged(nameof(IsIncomplete));
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static int? Port(string text)
        => int.TryParse(text, out var value) && value is > 0 and <= 65535 ? value : null;
}
