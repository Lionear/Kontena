using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>
/// A row in the Containers list — either a container or a Compose project heading (KON-159).
/// </summary>
/// <remarks>
/// <para>
/// Two row types in <b>one flat collection</b>, rather than a tree control. The list already has
/// filtering, sorting and an incremental sync that every page here shares; a <c>TreeDataGrid</c> would
/// mean a second implementation of all three, and the nesting is exactly one level deep.
/// </para>
/// <para>
/// Expanding is therefore not a control's job but this list's: a collapsed group simply does not put
/// its children in the collection. Both templates declare the same column widths, so the columns line
/// up across the two kinds of row.
/// </para>
/// </remarks>
public abstract class ContainerListRowViewModel : ObservableObject
{
    /// <summary>
    /// What this row sorts under. A group sorts under its project name, so it takes the place its name
    /// would have had — the alternative, groups first and loose containers after, reorders the whole
    /// list the moment somebody starts a stack.
    /// </summary>
    public abstract string SortKey { get; }
}
