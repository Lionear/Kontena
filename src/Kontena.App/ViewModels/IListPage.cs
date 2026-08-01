namespace Kontena.App.ViewModels;

/// <summary>A content page that shows a searchable list (Containers, Images, …).</summary>
public interface IListPage
{
    /// <summary>Two-way search text; the shared command-bar search binds to the active page.</summary>
    string SearchText { get; set; }

    /// <summary>True once the page has loaded its data at least once.</summary>
    bool HasLoaded { get; }

    /// <summary>Load (or reload) the page's data.</summary>
    Task LoadAsync();

    /// <summary>
    /// Whether this page does anything with <see cref="SearchText"/>.
    /// <para>
    /// Separate from the interface itself because reloading and searching are different capabilities:
    /// the Workloads dashboard wants Refresh but is cards rather than a list, and a search box that
    /// accepts text and ignores it is the dead-control problem (KON-117) in written form — worse than
    /// no box, because it looks like searching happened and found nothing (KON-164).
    /// </para>
    /// </summary>
    bool SupportsSearch => true;

    /// <summary>
    /// What the search box says when empty. Per page rather than one string for the whole app: the
    /// shared placeholder read "Search containers, images, volumes…" on a Kubernetes cluster, which
    /// names three things that do not exist there.
    /// </summary>
    string SearchPlaceholder => "Search…";
}
