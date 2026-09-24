using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kontena.App.ViewModels;

/// <summary>
/// The one-line "that landed" toast (KON-448).
/// <para>
/// An action whose whole effect is on the cluster leaves nothing behind in the window: a rollout
/// restart on a fast cluster is a PATCH that returns in milliseconds, the confirm closes, and the
/// only evidence it happened is a status that may not have moved yet. This says it did.
/// </para>
/// <para>
/// Deliberately not <see cref="UpdateViewModel"/>'s toast, which was the only one in the app: that
/// one is an offer with two buttons and a version behind it, and it stays up until answered. This
/// one is a sentence that goes away by itself. Sharing them would mean one of the two carrying
/// fields the other has no use for.
/// </para>
/// </summary>
public sealed partial class ActionToastViewModel : ViewModelBase
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    private int _shown;

    [ObservableProperty] private string _message = string.Empty;

    [ObservableProperty] private bool _isVisible;

    /// <summary>
    /// Say something, and take it away again after <paramref name="linger"/>.
    /// <para>
    /// Safe to call from a watch loop: the caller is not always on the UI thread, and a toast is
    /// never worth making every call site prove that it is.
    /// </para>
    /// </summary>
    public void Show(string message, TimeSpan? linger = null) =>
        Dispatcher.UIThread.Post(async () =>
        {
            Message = message;
            IsVisible = true;

            // A second toast during the first one replaces it rather than queueing: these announce
            // something that just happened, and one held back until its turn is a line about the
            // past pretending to be news.
            var mine = ++_shown;
            await Task.Delay(linger ?? Linger).ConfigureAwait(true);

            if (_shown == mine)
                IsVisible = false;
        });
}
