using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.App.ViewModels;

/// <summary>
/// The detail drawer: a panel that slides over the list rather than replacing it (KON-307).
/// <para>
/// A detail page used to be a page swap, which answers "what is this and is it healthy?" by taking
/// away the list you were reading. The drawer leaves the list where it was, and the row you opened
/// stays selected behind it — so closing it returns you to exactly the scroll position and filter you
/// had, without the shell having to remember any of that.
/// </para>
/// <para>
/// Its own slot rather than a second use of <see cref="Dialog"/>. A dialog is a question that must be
/// answered before anything else happens; a drawer is something you read and dismiss, and the two
/// stack — a Remove from inside the drawer opens a confirmation <i>over</i> it. One slot could not
/// hold both without one of them closing the other.
/// </para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The detail showing in the drawer, or null when it is closed.</summary>
    [ObservableProperty] private ViewModelBase? _detail;

    public bool IsDetailOpen => Detail is not null;

    partial void OnDetailChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        // The drawer owns what it shows. Opening a second detail without this leaks the first — and
        // for the pages that stream (KON-309, KON-310) that is a live log subscription, not just an
        // object.
        (oldValue as IDisposable)?.Dispose();

        OnPropertyChanged(nameof(IsDetailOpen));

        // Escape closes the drawer when no dialog is over it — same reason the dialog notifies here
        // (KON-201): a CanExecute that is never re-asked keeps answering "no".
        DismissCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Open a detail in the drawer.
    /// <para>
    /// Deliberately not recorded on the nav stack. A drawer is not somewhere you navigated to —
    /// Escape is the way out of it, and Back should still mean "the page before this list". Putting an
    /// overlay on the history would make Back close the drawer once and then leave the page, which is
    /// the mistimed-Escape problem the Dismiss command already avoids.
    /// </para>
    /// </summary>
    private void ShowDetail(ViewModelBase detail) => Detail = detail;

    /// <summary>Close the drawer and dispose what it held. The scrim, the ✕ and Escape all land here.</summary>
    [RelayCommand]
    private void CloseDetail() => Detail = null;
}
