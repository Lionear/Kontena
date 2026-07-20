using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Pull an image" modal: streams <see cref="IContainerEngine.PullImageAsync"/>
/// progress for an entered reference and refreshes the lists when it completes.
/// </summary>
public partial class PullImageViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onPulled;

    public PullImageViewModel(IContainerEngine engine, Action onClose, Func<Task> onPulled)
    {
        _engine = engine;
        _onClose = onClose;
        _onPulled = onPulled;
    }

    [ObservableProperty] private string _reference = string.Empty;
    [ObservableProperty] private bool _isPulling;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string? _error;

    public bool CanPull => !string.IsNullOrWhiteSpace(Reference) && !IsPulling;

    partial void OnReferenceChanged(string value)
    {
        OnPropertyChanged(nameof(CanPull));
        if (IsDone)
            IsDone = false;
    }

    partial void OnIsPullingChanged(bool value) => OnPropertyChanged(nameof(CanPull));

    [RelayCommand]
    private async Task PullAsync()
    {
        var reference = Reference.Trim();
        if (string.IsNullOrWhiteSpace(reference) || IsPulling)
            return;

        Error = null;
        IsDone = false;
        IsPulling = true;
        Status = "Preparing…";
        try
        {
            await foreach (var progress in _engine.PullImageAsync(reference))
                Status = FormatPull(progress);

            Status = "Pull complete";
            IsDone = true;
            await _onPulled();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = string.Empty;
        }
        finally
        {
            IsPulling = false;
        }
    }

    [RelayCommand]
    private void Close() => _onClose();

    private static string FormatPull(PullProgress progress)
    {
        if (progress.Total is > 0 && progress.Current is >= 0)
            return $"{progress.Status} {(int)(100.0 * progress.Current.Value / progress.Total.Value)}%";

        return progress.Status;
    }
}
