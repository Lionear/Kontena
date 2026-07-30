using System.Collections.ObjectModel;

namespace Kontena.App.ViewModels;

/// <summary>
/// A labelled section of the sidebar (KON-219).
/// <para>
/// The nav used to be one flat list. That was the right call at five entries and stopped being one at
/// ten: the cluster nav had grown to Overview, Nodes, Namespaces, Workloads, Pods, Services, Port
/// forwards, Resources, Apply manifest and Terminal, plus up to five more when Workloads expands — and
/// the only structure in it was the indent on those children. KON-169 made the sidebar scroll to fit
/// them, which treats the symptom.
/// </para>
/// <para>
/// A group with no label renders as a plain run of items, which is what the engine nav uses: five
/// entries do not need dividing, and a single heading over the whole list says nothing. That is a
/// decision rather than an omission — the sidebar has one shape, and how much of it is used depends on
/// how much there is to arrange.
/// </para>
/// </summary>
public sealed class NavGroup(string? label = null)
{
    /// <summary>Section heading, or null for an unlabelled run of items.</summary>
    public string? Label { get; } = label;

    public bool HasLabel => Label is { Length: > 0 };

    public ObservableCollection<NavItem> Items { get; } = [];
}
