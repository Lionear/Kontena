namespace Kontena.App.ViewModels;

/// <summary>
/// A detail view model whose subject can disappear while it is being shown — in the drawer, on the
/// full page, or (KON-308) in a window of its own. Each host decides what "gone" means for it: the
/// drawer and the full page close, a detached window stays open and says so. This only says when.
/// </summary>
public interface IDetachableDetail
{
    bool IsSourceGone { get; }
}
