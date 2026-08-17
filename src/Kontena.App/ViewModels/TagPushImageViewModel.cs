using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Sdk;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Tag and push" modal for one image (KON-387): give it another name, and send it to the registry
/// that name points at.
/// <para>
/// One dialog for both verbs rather than two, because that is the shape of the job — an image is pushed
/// under a name that names its registry, so retagging is the step before nearly every push. Tagging on
/// its own is still a button of its own; it is just not a separate modal.
/// </para>
/// </summary>
public partial class TagPushImageViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly string _id;
    private readonly Action _onClose;
    private readonly Func<Task> _onChanged;
    private readonly RegistryCredentials? _credentials;

    /// <summary>Set when a push went out unauthenticated, to explain a refusal the registry words badly.</summary>
    private string? _hint;

    /// <param name="id">Image id — what a tag is applied to, so a repository with several tags is unambiguous.</param>
    /// <param name="reference">Its current name, which is what the target starts out as.</param>
    /// <param name="credentials">Resolves the registry login for the target; null in tests.</param>
    public TagPushImageViewModel(
        IContainerEngine engine, string id, string reference, Action onClose, Func<Task> onChanged,
        RegistryCredentials? credentials = null)
    {
        _engine = engine;
        _id = id;
        _onClose = onClose;
        _onChanged = onChanged;
        _credentials = credentials;
        Source = reference;
        _target = reference;
    }

    /// <summary>The name the image has now, shown so it is clear what is being copied from.</summary>
    public string Source { get; }

    [ObservableProperty] private string _target;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string? _error;

    public bool CanApply => !string.IsNullOrWhiteSpace(Target) && !IsBusy;

    /// <summary>Where a push would go, so the registry is visible before the button is pressed.</summary>
    public string TargetRegistry => RegistryHost.For(Target);

    partial void OnTargetChanged(string value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(TargetRegistry));
        if (IsDone)
            IsDone = false;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    [RelayCommand]
    private Task TagAsync() => RunAsync(async target =>
    {
        await _engine.TagImageAsync(_id, target).ConfigureAwait(true);
        Status = $"Tagged as {target}";
    });

    [RelayCommand]
    private Task PushAsync() => RunAsync(async target =>
    {
        // The engine can only push a name that exists locally, so a changed target is tagged first —
        // otherwise "push" would fail on a reference the user just typed and never asked to keep local.
        if (!string.Equals(target, Source, StringComparison.Ordinal))
        {
            Status = $"Tagging as {target}…";
            await _engine.TagImageAsync(_id, target).ConfigureAwait(true);
        }

        var host = RegistryHost.For(target);

        // Null for a registry with no login, which a push rarely survives — but that is the registry's
        // answer to give, not ours to pre-empt (KON-114). What it says when it refuses, though,
        // ("unauthorized: authentication required") points at nothing the user can act on, so the one
        // thing they can act on is noted here in case this ends in an error.
        var credential = _credentials is null
            ? null
            : await _credentials.ForAsync(target).ConfigureAwait(true);

        if (credential is null)
        {
            _hint = $"No registry login is stored for {host}. Add one under Settings → Registries" +
                " if the push was refused.";
        }

        Status = credential is null
            ? $"Pushing to {host} anonymously…"
            : $"Pushing to {host} as {credential.Username}…";

        await foreach (var progress in _engine.PushImageAsync(target, credential))
            Status = FormatPush(progress);

        Status = "Push complete";
    });

    [RelayCommand]
    private void Close() => _onClose();

    /// <summary>
    /// The shared shell for both buttons: one busy flag, one error line, one refresh of the list behind
    /// the modal — the two commands differ only in what they do in the middle.
    /// </summary>
    private async Task RunAsync(Func<string, Task> action)
    {
        var target = Target.Trim();
        if (string.IsNullOrWhiteSpace(target) || IsBusy)
            return;

        Error = null;
        _hint = null;
        IsDone = false;
        IsBusy = true;
        Status = "Preparing…";
        try
        {
            await action(target).ConfigureAwait(true);
            IsDone = true;

            // The list behind the modal is now wrong either way: a tag added a name, and a push may have
            // added one on the way.
            await _onChanged().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = _hint is null ? ex.Message : $"{ex.Message} {_hint}";
            Status = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatPush(PushProgress progress)
    {
        if (progress.Total is > 0 && progress.Current is >= 0)
            return $"{progress.Status} {(int)(100.0 * progress.Current.Value / progress.Total.Value)}%";

        return progress.Status;
    }
}
