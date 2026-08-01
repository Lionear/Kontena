using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "New volume" modal (KON-91). Creating a volume up front is the other half of attaching one:
/// the Run modal can mount a named volume, but until now the only way to have a named volume was to
/// let some earlier container create it as a side effect.
/// </summary>
public partial class CreateVolumeViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onCreated;

    public CreateVolumeViewModel(IContainerEngine engine, Action onClose, Func<Task> onCreated)
    {
        _engine = engine;
        _onClose = onClose;
        _onCreated = onCreated;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _driver = "local";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>
    /// Drivers offered. Only <c>local</c> ships with both engines; anything else comes from a plugin
    /// the user installed, so this is an editable list rather than a fixed picker.
    /// </summary>
    public string[] Drivers { get; } = ["local"];

    public bool CanCreate => !string.IsNullOrWhiteSpace(Name) && !IsBusy;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));

        // The error described the name as it was when Create was pressed; typing makes it stale.
        if (Error is not null)
            Error = null;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name) || IsBusy)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            await _engine.CreateVolumeAsync(new CreateVolumeRequest
            {
                Name = name,
                Driver = string.IsNullOrWhiteSpace(Driver) ? "local" : Driver.Trim(),
            });

            await _onCreated();
            _onClose();
        }
        catch (Exception ex)
        {
            // Left open on purpose: a name that is taken or invalid is worth correcting in place
            // rather than retyping from scratch.
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClose();
}
