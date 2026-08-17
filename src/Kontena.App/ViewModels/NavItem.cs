using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>A single entry in the sidebar navigation.</summary>
public partial class NavItem : ObservableObject
{
    public NavItem(string key, string label, string iconKey, bool isChild = false)
    {
        Key = key;
        Label = label;
        IconKey = iconKey;
        IsChild = isChild;
    }

    /// <summary>Page key, e.g. "containers", "images".</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>Resource key of the Lucide geometry to show.</summary>
    public string IconKey { get; }

    /// <summary>
    /// A per-kind entry under Workloads. Kept as a flag rather than a nesting level: it decides
    /// nothing about layout any more — the group heading is the grouping — but it is still how the
    /// nav tells its own generated entries apart from the fixed ones when it rebuilds them.
    /// </summary>
    public bool IsChild { get; }

    /// <summary>
    /// Whether this entry came from a plugin (KON-331). Drawn as a badge, because which parts of the
    /// window are ours and which are someone else's is not something a user can otherwise tell.
    /// </summary>
    public bool IsPlugin { get; init; }

    /// <summary>Which plugin, for the badge's tooltip.</summary>
    public string PluginTip { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private string _count = string.Empty;

    /// <summary>
    /// Whether there is anything to show a badge for. The pill used to render on every row, empty
    /// included, which put a small grey plate next to every entry that has nothing to count (KON-219).
    /// </summary>
    public bool HasCount => Count.Length > 0;

    /// <summary>
    /// Whether the count is the loud kind (KON-207). A firing-alert count is not the same sort of
    /// number as "3 nodes": one is an inventory, the other is a queue of things wrong. This is the
    /// only badge in the sidebar allowed to shout, and it still carries a tooltip in words — the
    /// colour is never the only thing saying it.
    /// </summary>
    [ObservableProperty]
    private bool _isLoud;

    /// <summary>
    /// Draws a small marker on the item: something here needs looking at, independently of
    /// <see cref="Count"/>. Kept separate from the count because the count answers "how many are
    /// working", and those are different questions — a dropped tunnel is an event whether or not others
    /// are still up (KON-107).
    /// </summary>
    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>What the marker means, for the tooltip.</summary>
    [ObservableProperty]
    private string _attentionTip = string.Empty;

    /// <summary>Navigation command (shared), invoked with <see cref="Key"/> as parameter.</summary>
    public ICommand? Command { get; set; }
}
