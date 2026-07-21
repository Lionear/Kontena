using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.App.ViewModels;

/// <summary>
/// A small reusable confirmation modal for a (possibly destructive) action — title, message, and
/// a confirm button. The caller supplies what to run on confirm and how to close.
/// </summary>
public partial class ConfirmViewModel : ViewModelBase
{
    private readonly Func<Task> _onConfirm;
    private readonly Action _onClose;

    public ConfirmViewModel(
        string title, string message, string confirmLabel, Func<Task> onConfirm, Action onClose, bool destructive = false)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        Destructive = destructive;
        _onConfirm = onConfirm;
        _onClose = onClose;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmLabel { get; }

    /// <summary>Whether the confirm button uses the danger styling.</summary>
    public bool Destructive { get; }

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
}
