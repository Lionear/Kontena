using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// How a page asks the shell to put a confirmation in front of an action (KON-126). Set by the
    /// shell when the page is built.
    /// </summary>
    public Action<ConfirmRequest>? RequestConfirm { get; set; }

    /// <summary>
    /// Runs <paramref name="onConfirm"/> only once the user has confirmed. Without a handler nothing
    /// runs at all: a page that was never wired up must not quietly turn a confirm into a delete.
    /// </summary>
    protected void Confirm(
        string title, string message, string confirmLabel, Func<Task> onConfirm, bool destructive = true,
        IReadOnlyList<ConfirmDetail>? details = null)
        => RequestConfirm?.Invoke(
            new ConfirmRequest(title, message, confirmLabel, onConfirm, destructive, details));
}
