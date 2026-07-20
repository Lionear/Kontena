using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>A single entry in the sidebar navigation.</summary>
public partial class NavItem : ObservableObject
{
    public NavItem(string key, string label, string iconKey)
    {
        Key = key;
        Label = label;
        IconKey = iconKey;
    }

    /// <summary>Page key, e.g. "containers", "images".</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>Resource key of the Lucide geometry to show.</summary>
    public string IconKey { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _count = string.Empty;

    /// <summary>Navigation command (shared), invoked with <see cref="Key"/> as parameter.</summary>
    public ICommand? Command { get; set; }
}
