namespace Kontena.App.ViewModels;

/// <summary>
/// An honest placeholder for a cluster resource browser that isn't built yet. The grouped
/// switcher and cluster nav-mode ship now (KON-66/67); the per-resource grids arrive in KON-73.
/// </summary>
public sealed class ClusterPlaceholderViewModel : ViewModelBase
{
    public ClusterPlaceholderViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public string Message => $"The {Title} browser lands in KON-73 (Build the Kubernetes views).";
}
