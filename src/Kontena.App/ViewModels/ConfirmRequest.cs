namespace Kontena.App.ViewModels;

/// <summary>
/// A page's request for the shell to confirm an action before it runs (KON-126). The page owns the
/// wording — it is the one that knows what goes away and whether it comes back — and the shell owns
/// the modal.
/// </summary>
/// <param name="Title">Modal title, e.g. "Delete volume".</param>
/// <param name="Message">What happens, in full: what goes away and whether it is recoverable.</param>
/// <param name="ConfirmLabel">Label on the confirm button, e.g. "Delete".</param>
/// <param name="OnConfirm">Runs only after the user confirms.</param>
/// <param name="Destructive">Whether the confirm button uses the danger styling. Not everything worth
/// confirming destroys data — signing out of a registry does not — so this is not implied.</param>
/// <param name="Details">
/// What goes away, itemised (KON-162). Optional, and empty for most actions: removing one container
/// is fully said in a sentence. It earns its place where the count is the decision — taking a Compose
/// project down reaches several containers and networks at once.
/// </param>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmLabel,
    Func<Task> OnConfirm,
    bool Destructive = true,
    IReadOnlyList<ConfirmDetail>? Details = null);
