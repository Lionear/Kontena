using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "New Compose project" modal: pick a compose file, optionally name the project,
/// then stream <c>up</c> output over the CEAL into a console. On success it reloads the
/// Projects page so the freshly-created services appear.
/// </summary>
public partial class ComposeUpViewModel : ViewModelBase, IDisposable
{
    private const int MaxConsoleLines = 2000;

    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onUp;
    private CancellationTokenSource? _cts;

    public ComposeUpViewModel(IContainerEngine engine, Action onClose, Func<Task> onUp)
    {
        _engine = engine;
        _onClose = onClose;
        _onUp = onUp;
    }

    // ── Config ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _composeFile = string.Empty;
    [ObservableProperty] private string _projectName = string.Empty;
    [ObservableProperty] private bool _build;
    [ObservableProperty] private bool _forceRecreate;

    partial void OnComposeFileChanged(string value)
    {
        OnPropertyChanged(nameof(CanUp));

        // Default the project name to the compose file's folder (overridable), like Compose does.
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            var dir = Path.GetDirectoryName(value);
            if (!string.IsNullOrEmpty(dir))
                ProjectName = Path.GetFileName(dir.TrimEnd('/', '\\')).ToLowerInvariant();
        }
    }

    /// <summary>Called by the view's file picker.</summary>
    public void SetComposeFile(string path) => ComposeFile = path;

    // ── State ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string _statusLine = string.Empty;

    public bool NotStarted => !IsRunning && !IsDone && !IsFailed;
    public bool HasOutput => IsRunning || IsDone || IsFailed;
    public bool CanUp => !string.IsNullOrWhiteSpace(ComposeFile) && NotStarted;

    partial void OnIsRunningChanged(bool value) => RaiseState();
    partial void OnIsDoneChanged(bool value) => RaiseState();
    partial void OnIsFailedChanged(bool value) => RaiseState();

    private void RaiseState()
    {
        OnPropertyChanged(nameof(NotStarted));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(CanUp));
    }

    public ObservableCollection<string> Console { get; } = [];

    // ── Up ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task UpAsync()
    {
        if (!CanUp)
            return;

        var file = ComposeFile.Trim();
        if (!File.Exists(file))
        {
            Fail($"Compose file not found: {file}");
            return;
        }

        Console.Clear();
        StatusLine = "Starting…";
        IsDone = false;
        IsFailed = false;
        IsRunning = true;

        var request = new ComposeUpRequest
        {
            ComposeFilePath = file,
            ProjectName = string.IsNullOrWhiteSpace(ProjectName) ? null : ProjectName.Trim(),
            Build = Build,
            ForceRecreate = ForceRecreate,
        };

        _cts = new CancellationTokenSource();
        try
        {
            Services.Diag.Action("compose up", request.ProjectName ?? file);

            await foreach (var progress in _engine.ComposeUpAsync(request, _cts.Token))
            {
                Append(progress.Text);
                StatusLine = progress.Text;
                if (progress.Error is not null)
                {
                    IsFailed = true;
                    return;
                }
            }

            if (!IsFailed)
                Complete();
        }
        catch (OperationCanceledException)
        {
            Append("[compose up cancelled]");
            Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Close after a run: reload the Projects page first so new services show.</summary>
    [RelayCommand]
    private async Task CloseAndRefreshAsync()
    {
        await _onUp();
        _onClose();
    }

    /// <summary>Close without having started (nothing to refresh).</summary>
    [RelayCommand]
    private void Close() => _onClose();

    private void Complete()
    {
        IsDone = true;
        StatusLine = "Project is up.";
    }

    private void Fail(string message)
    {
        Append(message);
        StatusLine = message;
        IsFailed = true;
    }

    private void Append(string line)
    {
        Console.Add(line);
        if (Console.Count > MaxConsoleLines)
            Console.RemoveAt(0);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
