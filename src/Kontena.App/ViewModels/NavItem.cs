using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>A single entry in the sidebar navigation.</summary>
public partial class NavItem : ObservableObject
{
    public NavItem(string label, string iconKey, bool isSelected = false)
    {
        Label = label;
        IconKey = iconKey;
        IsSelected = isSelected;
    }

    public string Label { get; }

    /// <summary>Resource key of the Lucide geometry to show.</summary>
    public string IconKey { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _count = string.Empty;
}
