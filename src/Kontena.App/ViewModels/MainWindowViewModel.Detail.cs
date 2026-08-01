using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.App.ViewModels;

/// <summary>
/// The detail drawer: a panel that slides over the list rather than replacing it (KON-307).
/// <para>
/// A detail page used to be a page swap, which answers "what is this and is it healthy?" by taking
/// away the list you were reading. The drawer leaves the list where it was, and closing it returns
/// you to exactly the scroll position and filter you had, without the shell having to remember any
/// of that.
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
    /// <summary>Set while the drawer hands its detail to another host, so closing does not dispose it.</summary>
    private bool _handingOverDetail;

    /// <summary>What the drawer shows, remembered so the full page can be labelled and replayed.</summary>
    private string _detailLabel = string.Empty;
    private object? _detailTarget;

    /// <summary>The detail showing in the drawer, or null when it is closed.</summary>
    [ObservableProperty] private ViewModelBase? _detail;

    public bool IsDetailOpen => Detail is not null;

    /// <summary>
    /// The drawer's width, dragged by its left edge and remembered across launches. Not a Settings
    /// field: how much of the list you want kept in view depends on the list you are looking at.
    /// </summary>
    [ObservableProperty] private double _detailWidth = 540;

    /// <summary>Narrow enough to leave a list usable, wide enough that a detail header still fits.</summary>
    private const double MinDetailWidth = 460;
    private const double MaxDetailWidth = 1200;

    partial void OnDetailChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        // The drawer owns what it shows unless it is handing it over. Opening a second detail without
        // this leaks the first — and for the pages that stream (KON-309, KON-310) that is a live log
        // subscription, not just an object.
        if (!_handingOverDetail)
            (oldValue as IDisposable)?.Dispose();

        OnPropertyChanged(nameof(IsDetailOpen));

        // Escape closes the drawer when no dialog is over it — same reason the dialog notifies here
        // (KON-201): a CanExecute that is never re-asked keeps answering "no".
        DismissCommand.NotifyCanExecuteChanged();
        OpenDetailAsPageCommand.NotifyCanExecuteChanged();
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
    /// <param name="label">For the history, once it is opened as a full page — "node gke-prod-cp-1".</param>
    /// <param name="target">The object shown, so the step can be dropped when it is deleted.</param>
    private void ShowDetail(ViewModelBase detail, string label, object? target = null)
    {
        _detailLabel = label;
        _detailTarget = target;
        Detail = detail;
    }

    /// <summary>Close the drawer and dispose what it held. The scrim, the ✕ and Escape all land here.</summary>
    [RelayCommand]
    private void CloseDetail() => Detail = null;

    /// <summary>
    /// Take the detail out of the drawer without disposing it, for a host that is taking it over —
    /// the full page here, its own window in KON-308.
    /// </summary>
    private ViewModelBase? TakeDetail()
    {
        var detail = Detail;
        if (detail is null)
            return null;

        _handingOverDetail = true;
        try
        {
            Detail = null;
        }
        finally
        {
            _handingOverDetail = false;
        }

        return detail;
    }

    /// <summary>
    /// Show the drawer's detail as a full page (KON-307).
    /// <para>
    /// The drawer is deliberately narrow, and some of what these pages show — a YAML tab, a wide
    /// events table — wants the window. This is the same view model, moved: no reload, and whatever
    /// tab you were on stays the tab you are on.
    /// </para>
    /// <para>
    /// <i>This</i> is a navigation, so it does go on the history: from a full page, Back means the
    /// list you came from.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDetailOpen))]
    private void OpenDetailAsPage()
    {
        if (TakeDetail() is not { } detail)
            return;

        Arrived(_detailLabel, () => CurrentPage = detail, _detailTarget);
        CurrentPage = detail;
    }

    /// <summary>
    /// Widen or narrow the drawer by a drag on its left edge, and remember where it was let go.
    /// Written on every drag rather than on release: a drag that ends in a crash is still a preference,
    /// and the store already coalesces writes.
    /// </summary>
    public void ResizeDetail(double delta)
    {
        var width = Math.Clamp(DetailWidth + delta, MinDetailWidth, MaxDetailWidth);
        if (Math.Abs(width - DetailWidth) < 0.5)
            return;

        DetailWidth = width;
        _store.Update(s => s with { DetailDrawerWidth = width });
    }
}
