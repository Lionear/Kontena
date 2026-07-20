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
}
