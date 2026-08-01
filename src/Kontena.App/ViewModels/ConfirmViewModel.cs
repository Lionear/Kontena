using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.App.ViewModels;

/// <summary>
/// A small reusable confirmation modal for a (possibly destructive) action — title, message, and
/// a confirm button. The caller supplies what to run on confirm and how to close.
/// </summary>
public partial class ConfirmViewModel : ViewModelBase, IPrimaryAction
{
    private readonly Func<Task> _onConfirm;
    private readonly Action _onClose;

    public ConfirmViewModel(
        string title, string message, string confirmLabel, Func<Task> onConfirm, Action onClose,
        bool destructive = false, IReadOnlyList<ConfirmDetail>? details = null)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        Destructive = destructive;
        Details = details ?? [];
        _onConfirm = onConfirm;
        _onClose = onClose;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmLabel { get; }

    /// <summary>Whether the confirm button uses the danger styling, and shows the warning mark.</summary>
    public bool Destructive { get; }

    /// <summary>What goes away, itemised. Empty for the many actions a sentence already covers.</summary>
    public IReadOnlyList<ConfirmDetail> Details { get; }

    public bool HasDetails => Details.Count > 0;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            await _onConfirm();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClose();

    // Enter confirms (KON-172). Deliberately the same guard the button uses, so a busy dialog cannot
    // be double-fired by a second Enter — and a destructive confirm answers Enter like any other,
    // because it is the button the dialog is asking about, not a shortcut around the question.
    public bool CanInvokePrimary => !IsBusy;

    public void InvokePrimary() => ConfirmCommand.Execute(null);
}
