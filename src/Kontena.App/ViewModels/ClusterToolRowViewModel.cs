using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Tooling;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>One tool on the Local clusters page: what state it is in and what can be done about it.</summary>
public sealed partial class ClusterToolRowViewModel : ObservableObject
{
    private readonly ClusterToolingViewModel _parent;
    private ToolReadiness _readiness;
    private ToolUpdate? _update;

    public ClusterToolRowViewModel(ToolReadiness readiness, ClusterToolingViewModel parent, string purpose)
    {
        _readiness = readiness;
        _parent = parent;
        Purpose = purpose;
    }

    /// <summary>What this tool is for, in the user's terms rather than the project's own blurb.</summary>
    public string Purpose { get; }

    public ExternalTool Tool => _readiness.Tool;
    public string Name => _readiness.Tool.Name;

    /// <summary>What the installed copy answered when asked, for comparing against a release.</summary>
    public string? Version => _readiness.Version;

    /// <summary>Replace the readiness after a re-check, keeping the row in place.</summary>
    public void Update(ToolReadiness readiness)
    {
        _readiness = readiness;
        foreach (var property in new[]
                 {
                     nameof(StateText), nameof(StateBrush), nameof(Detail), nameof(IsMissing),
                     nameof(IsReady), nameof(IsOutdated), nameof(IsUnusable), nameof(CanInstall),
                     nameof(CanDownload), nameof(CanRemove), nameof(HintCommand), nameof(HasHint),
                     nameof(DocumentationUrl), nameof(HasDocumentation), nameof(Version),
                     nameof(IsKontenaManaged), nameof(CanHandOver), nameof(CanUseSystemAgain),
                     nameof(HasUpdate), nameof(UpdateText),
                 })
        {
            OnPropertyChanged(property);
        }

        // The command is gated on CanRemove, so it has to be told the answer changed — otherwise a
        // row that just gained (or lost) a managed copy keeps the previous button state.
        RemoveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the publisher's newest release is (KON-153). Separate from <see cref="Update(ToolReadiness)"/>
    /// because it arrives later and over the network: the row is drawn from what is on disk, and this
    /// fills in behind it or never arrives at all.
    /// </summary>
    public void SetUpdate(ToolUpdate? update)
    {
        _update = update;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(CanHandOver));
    }

    /// <summary>
    /// A newer release exists. Only ever a line of text — never a colour, never a badge. A tool one
    /// release behind does its job, and dressing that up as a problem trains people to ignore the
    /// states that are one.
    /// </summary>
    public bool HasUpdate => _update is { IsNewer: true };

    public string UpdateText => _update is { IsNewer: true } update
        ? $"{Shorten(update.Latest)} is available"
        : string.Empty;

    /// <summary>True when this tool was handed to Kontena and its copy wins over a system install.</summary>
    public bool IsKontenaManaged => _readiness.Preferred;

    /// <summary>
    /// Whether handing this one over is worth offering: there is an install Kontena is not in charge
    /// of, and a publisher it can fetch from. Not offered for a tool that is simply missing — that is
    /// what Install and Download are for, and a third verb for the same act is three ways to be unsure.
    /// </summary>
    public bool CanHandOver =>
        !_readiness.Preferred && !_readiness.Managed && _readiness.Usable && _readiness.CanBeDownloaded;

    public bool CanUseSystemAgain => _readiness.Preferred;

    public bool IsMissing => _readiness.State == ToolState.Missing;
    public bool IsReady => _readiness.State == ToolState.Ready;
    public bool IsOutdated => _readiness.State == ToolState.Outdated;
    public bool IsUnusable => _readiness.State == ToolState.Unusable;

    public string StateText => _readiness.State switch
    {
        ToolState.Ready => $"Detected · {Shorten(_readiness.Version)}",
        ToolState.Outdated => $"{Shorten(_readiness.Version)} · older than Kontena expects",
        ToolState.Unusable => "Found, but it will not run",
        _ => "Not installed",
    };

    public IBrush StateBrush => new SolidColorBrush(Color.Parse(_readiness.State switch
    {
        ToolState.Ready => "#34D399",
        ToolState.Outdated => "#F5B14C",
        ToolState.Unusable => "#F87171",
        _ => "#808B9B",
    }));

    /// <summary>
    /// The line under the name. A managed copy says so — someone reading this needs to know which
    /// installs their package manager is looking after and which one Kontena is.
    /// </summary>
    public string Detail => _readiness.State switch
    {
        ToolState.Missing => Purpose,
        _ when _readiness.Preferred => $"Kontena's copy, chosen over the system install · {_readiness.Path}",
        _ when _readiness.Managed => $"Kontena's own copy · {_readiness.Path}",
        _ => _readiness.Path ?? Purpose,
    };

    public bool CanInstall => IsMissing && _readiness.Hint is { Manager: not PackageManager.Manual };
    public bool CanDownload => IsMissing && _readiness.CanBeDownloaded;
    public bool CanRemove => _readiness.Managed;

    public bool HasHint => _readiness.Hint is { Manager: not PackageManager.Manual };
    public string HintCommand => _readiness.Hint?.CommandLine ?? string.Empty;

    public bool HasDocumentation => _readiness.Tool.DocumentationUrl is not null;
    public string DocumentationUrl => _readiness.Tool.DocumentationUrl ?? string.Empty;

    /// <summary>
    /// What is lost by carrying on with a version that is too old. The tool's own wording where it has
    /// one — "the cluster settings it writes" is true of kind and minikube and nonsense for kubectl,
    /// which Kontena never builds a cluster with.
    /// </summary>
    public string OutdatedConsequence =>
        _readiness.Tool.OutdatedConsequence
        ?? $"Kontena needs {_readiness.Tool.MinimumVersion} or newer for the cluster settings it writes. " +
           "On an older one those are ignored and the cluster comes up on the tool's own defaults; " +
           "everything else works.";

    [RelayCommand]
    private Task Install() => _parent.InstallAsync(this, _readiness.Hint!);

    [RelayCommand]
    private Task Download() => _parent.DownloadAsync(this);

    /// <summary>
    /// Guarded twice on purpose: <c>CanExecute</c> greys the button out, but
    /// <c>RelayCommand.Execute</c> runs the delegate regardless of it — that is the UI's check, not
    /// the command's. Without the second guard a hidden control still fires when something invokes it
    /// directly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (CanRemove)
            _parent.ConfirmRemove(this);
    }

    [RelayCommand]
    private void Documentation() => _parent.OpenDocumentation(DocumentationUrl);

    /// <summary>Hand this tool to Kontena: fetch a copy if there is none, then let it win over PATH.</summary>
    [RelayCommand]
    private Task HandOver() => _parent.PreferManagedAsync(this);

    /// <summary>Give it back. The copy stays where it is; it simply stops being the one that runs.</summary>
    [RelayCommand]
    private Task UseSystem() => _parent.PreferSystemAsync(this);

    /// <summary>
    /// Tools answer with a paragraph — <c>kind v0.31.0 go1.25.5 linux/amd64</c>. The first word that
    /// looks like a version is the part anyone reads.
    /// </summary>
    private static string Shorten(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        foreach (var word in version.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word.Any(char.IsDigit) && word.Contains('.', StringComparison.Ordinal))
                return word;

        return version.Trim();
    }
}
