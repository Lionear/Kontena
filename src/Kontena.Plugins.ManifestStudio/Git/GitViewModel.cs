using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>
/// Drives <see cref="GitCli"/> for one workspace folder. Errors land in <see cref="Error"/> rather
/// than throwing out of a command, same reasoning as <c>PlanApplyViewModel</c> (KON-294): a faulted
/// async command disables itself in most MVVM toolkits, which reads as "the button stopped working".
/// <para>
/// Being behind the remote is surfaced through <see cref="Status"/>, never blocks a command here —
/// Plan §7 is explicit that a stale local branch is a warning, not a reason to refuse committing.
/// </para>
/// </summary>
public sealed partial class GitViewModel(GitCli git, string repositoryPath) : ObservableObject
{
    [ObservableProperty]
    private GitStatus? _status;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _commitMessage = string.Empty;

    [RelayCommand]
    private Task Refresh() => RunAsync(async ct =>
    {
        var result = await git.StatusAsync(repositoryPath, ct);
        if (result.Ok)
            Status = result.Status;

        return result.Error;
    });

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task Commit() => RunAndRefreshAsync(ct => git.CommitAsync(repositoryPath, CommitMessage, ct), clearCommitMessage: true);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAndPush() =>
        RunAndRefreshAsync(ct => git.CommitAndPushAsync(repositoryPath, CommitMessage, ct), clearCommitMessage: true);

    [RelayCommand]
    private Task Push() => RunAndRefreshAsync(ct => git.PushAsync(repositoryPath, ct));

    [RelayCommand]
    private Task Pull() => RunAndRefreshAsync(ct => git.PullAsync(repositoryPath, ct));

    private bool CanCommit() => CommitMessage.Length > 0;

    partial void OnCommitMessageChanged(string value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Runs a command that changes the repository, then always re-checks status afterwards —
    /// <c>git</c> itself is the source of truth for what happened, never a value predicted locally.
    /// <paramref name="clearCommitMessage"/> is only ever true for the two commit commands, and only
    /// takes effect once the commit it belongs to actually succeeded.</summary>
    private async Task RunAndRefreshAsync(
        Func<CancellationToken, ValueTask<GitCommandResult>> operation, bool clearCommitMessage = false)
    {
        IsBusy = true;
        Error = null;

        try
        {
            var result = await operation(default);
            Error = result.Error;
            if (result.Ok && clearCommitMessage)
                CommitMessage = string.Empty;

            var status = await git.StatusAsync(repositoryPath);
            if (status.Ok)
                Status = status.Status;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<string?>> operation)
    {
        IsBusy = true;
        Error = null;

        try
        {
            Error = await operation(default);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
