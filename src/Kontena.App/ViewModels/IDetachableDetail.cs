namespace Kontena.App.ViewModels;

/// <summary>
/// A detail view model whose subject can disappear while it is being shown — in the drawer, on the
/// full page, or (KON-308) in a window of its own. Each host decides what "gone" means for it: the
/// drawer and the full page close, a detached window stays open and says so. This only says when.
/// </summary>
public interface IDetachableDetail
{
    bool IsSourceGone { get; }

    /// <summary>A stable identity for "the same real-world object" across a rebuild — a list reload
    /// constructs new record instances for the same pod/node/etc, so reference equality on the domain
    /// object cannot be used to recognise "already open in a window" (KON-308).</summary>
    string DetailKey { get; }
}
