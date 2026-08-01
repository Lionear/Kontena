using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Sdk.Models;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>Where an update has got to. Drives the card, the toast and the sidebar entry alike.</summary>
public enum UpdateStage
{
    /// <summary>Nothing found, or nothing checked yet. No sidebar entry, no toast.</summary>
    None = 0,

    /// <summary>A newer version exists and has not been fetched yet.</summary>
    Available,

    /// <summary>Fetching it.</summary>
    Downloading,

    /// <summary>Downloaded and verified; only a restart is left.</summary>
    Ready,

    /// <summary>The check or the download did not finish. Recoverable — the card offers a retry.</summary>
    Failed,

    /// <summary>A check the user asked for came back empty. Says so once, then fades to None.</summary>
    UpToDate,
}

/// <summary>
/// The in-app updater (KON-110): one long-lived view model behind three surfaces — the sidebar
/// entry, the toast, and the card that opens from either. They all describe the same update, so
/// they read one <see cref="Stage"/> rather than keeping their own copies of it.
/// </summary>
[SuppressMessage("Reliability", "CA1001",
    Justification = "Deliberately not IDisposable: this view model outlives the card and is shown in the shared modal slot, which disposes whatever it holds on close — implementing IDisposable would tear down the updater the first time the card is dismissed. The CTS is disposed where it is used.")]
public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _service;
    private readonly SettingsStore _store;
    private readonly Func<KontenaSettings> _settings;
    private readonly Action _openCard;
    private readonly Action _closeCard;

    private CancellationTokenSource? _download;

    /// <summary>The channel the last check used; what a failure message names.</summary>
    private UpdateChannel _channel;

    /// <param name="openCard">Shows the card in the shell's modal slot.</param>
    /// <param name="closeCard">Hides it again.</param>
    public UpdateViewModel(
        IUpdateService service, SettingsStore store, Func<KontenaSettings> settings,
        Action openCard, Action closeCard)
    {
        _service = service;
        _store = store;
        _settings = settings;
        _openCard = openCard;
        _closeCard = closeCard;
    }

    // ── State ───────────────────────────────────────────────────────────────

    [ObservableProperty] private UpdateStage _stage = UpdateStage.None;

    partial void OnStageChanged(UpdateStage value)
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(HasSidebarEntry));
        OnPropertyChanged(nameof(SidebarLabel));
        OnPropertyChanged(nameof(SidebarPill));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(SecondaryLabel));
        OnPropertyChanged(nameof(ShowSecondary));
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    public bool IsAvailable => Stage == UpdateStage.Available;
    public bool IsDownloading => Stage == UpdateStage.Downloading;
    public bool IsReady => Stage == UpdateStage.Ready;
    public bool IsFailed => Stage == UpdateStage.Failed;
    public bool IsUpToDate => Stage == UpdateStage.UpToDate;

    /// <summary>Version on offer, e.g. <c>0.2.0</c>.</summary>
    [ObservableProperty] private string _version = string.Empty;

    /// <summary>Release notes from the package, already markdown.</summary>
    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private long _sizeBytes;

    /// <summary>Download size as the header shows it; empty when the feed did not give one.</summary>
    public string SizeText => SizeBytes > 0 ? Format.Size(SizeBytes) : string.Empty;

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(SizeText));

    /// <summary>What went wrong, in the words the card shows.</summary>
    [ObservableProperty] private string _error = string.Empty;

    public string CurrentVersion => _service.CurrentVersion;

    /// <summary>
    /// When the running build was made, beside the version it is being compared against. On a
    /// nightly "am I current?" is really "how old is this build?", and two nightlies a day apart no
    /// longer say so in their version (KON-268).
    /// </summary>
    public string BuildDate { get; } = AppVersion.BuiltOn;

    public bool HasBuildDate => BuildDate.Length > 0;

    /// <summary>The whole of what the card says when there is nothing to offer.</summary>
    public string UpToDateSummary => HasBuildDate
        ? $"You are on {CurrentVersion}, {BuildDate} — the newest release on your channel."
        : $"You are on {CurrentVersion}, the newest release on your channel.";

    /// <summary>The stream this build came from — what a fresh install follows (KON-123).</summary>
    public UpdateChannel BuildChannel => _service.BuildChannel;

    /// <summary>
    /// False for an install that cannot replace itself — a distro package, or a build directory.
    /// The card then explains that and links to the downloads instead of offering a restart.
    /// </summary>
    public bool CanSelfUpdate => _service.Support == UpdateSupport.Supported;

    public string UnsupportedReason =>
        $"This copy of Kontena ({CurrentVersion}) is not managed by its own updater — it was unpacked "
        + "from an archive, or installed by your distribution's package manager. New versions come "
        + "from wherever this one did.";

    // ── Progress ────────────────────────────────────────────────────────────

    [ObservableProperty] private int _percent;
    [ObservableProperty] private string _progressDetail = string.Empty;

    // ── Toast ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the toast is up. It appears once per version: the sidebar entry keeps the update
    /// visible afterwards, so re-announcing it on every launch would be nagging, not informing.
    /// </summary>
    [ObservableProperty] private bool _isToastVisible;

    /// <summary>
    /// Whether the sidebar carries an entry. Failed counts: the update is still there, and without
    /// the entry a dismissed failure card leaves no way back to it short of Settings.
    /// </summary>
    public bool HasSidebarEntry => Stage
        is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Failed;

    /// <summary>The sidebar entry mirrors the stage rather than always reading "Update available".</summary>
    public string SidebarLabel => Stage switch
    {
        UpdateStage.Downloading => "Downloading…",
        UpdateStage.Ready => "Restart to update",
        UpdateStage.Failed => "Update failed",
        _ => "Update available",
    };

    public string SidebarPill => Stage == UpdateStage.Downloading ? $"{Percent}%" : Version;

    partial void OnPercentChanged(int value) => OnPropertyChanged(nameof(SidebarPill));
    partial void OnVersionChanged(string value) => OnPropertyChanged(nameof(SidebarPill));

    // ── Card buttons ────────────────────────────────────────────────────────

    /// <summary>The card's primary action, which is a different verb at every stage.</summary>
    public string PrimaryLabel => Stage switch
    {
        UpdateStage.Downloading => "Installing…",
        UpdateStage.Ready => "Restart now",
        UpdateStage.Failed => "Try again",
        UpdateStage.UpToDate => "Close",
        _ => "Download and install",
    };

    /// <summary>
    /// Whether the secondary button is offered at all. "Remind me later" makes no sense next to a
    /// card that says you are up to date, and neither does it when nothing can be applied here.
    /// </summary>
    public bool ShowSecondary => CanSelfUpdate && Stage != UpdateStage.UpToDate;

    public string SecondaryLabel => Stage switch
    {
        UpdateStage.Downloading => "Cancel",
        UpdateStage.Ready => "Install on next launch",
        _ => "Remind me later",
    };

    /// <summary>A download in flight owns the primary button; everything else can be clicked.</summary>
    public bool CanRunPrimary => Stage != UpdateStage.Downloading;

    // ── Checking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Look for a new version. <paramref name="userAsked"/> separates the check on launch from the
    /// one behind the button in Settings: only the second reports "you are up to date", and only
    /// the first is allowed to stay quiet about a version the user has already dismissed.
    /// </summary>
    public async Task CheckAsync(bool userAsked = false, CancellationToken ct = default)
    {
        if (!CanSelfUpdate)
        {
            if (userAsked)
                _openCard();
            return;
        }

        try
        {
            var settings = _settings();

            // Remembered so a failure two steps later — the download — can still say which channel
            // it was talking about.
            _channel = settings.ResolvedUpdateChannel(_service.BuildChannel);
            var found = await _service.CheckAsync(_channel, ct).ConfigureAwait(true);

            if (found is null)
            {
                Stage = userAsked ? UpdateStage.UpToDate : UpdateStage.None;
                if (userAsked)
                    _openCard();
                return;
            }

            Version = found.Version;
            SizeBytes = found.SizeBytes;
            Notes = PlainNotes(found.NotesMarkdown);
            Stage = UpdateStage.Available;

            // A version the user waved away still gets its sidebar entry, but not a second toast.
            IsToastVisible = userAsked || settings.DismissedUpdateVersion != found.Version;

            if (userAsked)
            {
                IsToastVisible = false;
                _openCard();
            }

            if (settings.AutoDownloadUpdates)
                await DownloadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-check is not a failure worth a card.
        }
        catch (Exception error)
        {
            Error = Describe(error, _channel);
            Stage = UpdateStage.Failed;
            if (userAsked)
                _openCard();
        }
    }

    // ── Downloading ─────────────────────────────────────────────────────────

    private async Task DownloadAsync()
    {
        _download?.Cancel();
        _download?.Dispose();
        _download = new CancellationTokenSource();
        var mine = _download;

        Percent = 0;
        ProgressDetail = string.Empty;
        Stage = UpdateStage.Downloading;

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                // A superseded download keeps running until its cancellation lands; its numbers must
                // not fight with the live one's over the same progress bar.
                if (!ReferenceEquals(_download, mine))
                    return;

                Percent = p.Percent;
                ProgressDetail = p.TotalBytes > 0
                    ? $"{Format.Size(p.BytesReceived)} of {Format.Size(p.TotalBytes)}"
                      + (p.BytesPerSecond > 0 ? $" · {Format.Size((long)p.BytesPerSecond)}/s" : string.Empty)
                    : string.Empty;
            });

            await _service.DownloadAsync(progress, mine.Token).ConfigureAwait(true);

            // Every write below is guarded the same way: only the download that is still the current
            // one may set the stage. A cancel is usually a *replacement* — a second check starting a
            // fresh download — and the loser's continuation runs after the winner has already set
            // Downloading, so an unguarded write would roll a live download back to "on offer".
            if (ReferenceEquals(_download, mine))
                Stage = UpdateStage.Ready;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_download, mine))
                Stage = UpdateStage.Available;
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(_download, mine))
                return;

            Error = Describe(error, _channel);
            Stage = UpdateStage.Failed;
        }
        finally
        {
            // Only if no later download has replaced it — otherwise this disposes the live one.
            if (ReferenceEquals(_download, mine))
            {
                _download = null;
                mine.Dispose();
            }
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenCard()
    {
        IsToastVisible = false;
        _openCard();
    }

    /// <summary>Dismiss the toast without opening anything — and remember not to raise it again.</summary>
    [RelayCommand]
    private void DismissToast()
    {
        IsToastVisible = false;
        if (!string.IsNullOrEmpty(Version))
            _store.Update(s => s with { DismissedUpdateVersion = Version });
    }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (Stage)
        {
            case UpdateStage.Ready:
                _service.ApplyAndRestart();
                break;

            case UpdateStage.Failed:
                Stage = UpdateStage.Available;
                await (string.IsNullOrEmpty(Version)
                    ? CheckAsync(userAsked: true)
                    : DownloadAsync()).ConfigureAwait(true);
                break;

            case UpdateStage.UpToDate:
                _closeCard();
                break;

            default:
                await DownloadAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    private void Secondary()
    {
        switch (Stage)
        {
            case UpdateStage.Downloading:
                _download?.Cancel();
                break;

            case UpdateStage.Ready:
                _service.ApplyOnNextLaunch();
                _closeCard();
                break;

            default:
                _closeCard();
                break;
        }
    }

    [RelayCommand]
    private void Close() => _closeCard();

    /// <summary>Open the releases page — the way out when this install cannot update itself.</summary>
    [RelayCommand]
    private static void OpenDownloads() => Browser.OpenUrl("https://github.com/Lionear/Kontena/releases");

    /// <summary>
    /// Release notes as readable text. They arrive as markdown, written for a GitHub release page,
    /// and the card is a plain <c>TextBlock</c> — so left alone they read as literal
    /// <c>**Kubernetes clusters.**</c>. Stripping the syntax is closer to the intent than showing it;
    /// rendering markdown properly would mean a dependency for one paragraph of text.
    /// </summary>
    private static string PlainNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = Regex.Replace(text, @"^#{1,6}\s*", string.Empty, RegexOptions.Multiline);   // headings
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1", RegexOptions.Singleline);       // bold
        text = Regex.Replace(text, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*", "$1");             // italics
        text = Regex.Replace(text, "`(.+?)`", "$1");                                       // code spans
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", "$1");                           // links
        text = Regex.Replace(text, @"^\s*[-*]\s+", "•  ", RegexOptions.Multiline);         // bullets
        text = Regex.Replace(text, @"\n{3,}", "\n\n");                                     // runs of blanks
        return text.Trim();
    }

    /// <summary>
    /// An exception in the words of someone waiting for a download, not a stack trace. The detail
    /// still matters — "no space left" and "no network" need different actions from the reader —
    /// so the message is kept, only framed.
    /// <para>
    /// Every <see cref="HttpRequestException"/> used to read "check your connection" (KON-163). Four
    /// different failures arrived through that one sentence and three of them sent the reader after
    /// their own network: a rate limit is a wait, a 404 on a rolling channel is us publishing right
    /// now, and a refused TLS handshake is whatever sits between. The status code that decides this
    /// is on the exception — it was being thrown away. Same shape as KON-161: a category shown where
    /// a diagnosis was available.
    /// </para>
    /// </summary>
    internal static string Describe(Exception error, UpdateChannel channel) => error switch
    {
        HttpRequestException http => Http(http, channel),
        UnauthorizedAccessException or IOException => $"Could not write the update: {error.Message}",
        _ => error.Message,
    };

    private static string Http(HttpRequestException error, UpdateChannel channel) => error.StatusCode switch
    {
        HttpStatusCode.NotFound when channel is UpdateChannel.Nightly or UpdateChannel.Preview =>
            $"The {Name(channel)} build is being replaced right now, so its files are briefly not there."
            + " Nothing is wrong with this copy — try again in a few minutes.",

        HttpStatusCode.NotFound =>
            $"No {Name(channel)} release has been published yet, so there is nothing to update to.",

        // GitHub allows 60 anonymous requests an hour per address, which a shared office or VPN
        // address reaches without any one person noticing.
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
            "The update server is refusing further requests from this network for now — its hourly"
            + " limit is shared by everyone on the same address. Try again later.",

        { } status =>
            $"The update server answered {(int)status} ({status}). This is on our side, not yours.",

        // No status at all: the request never got an answer. A TLS failure is the one worth naming,
        // because "check your connection" sends someone to a router that is working fine.
        _ when error.InnerException is AuthenticationException =>
            "The secure connection to the update server was refused. A proxy or antivirus that"
            + " inspects HTTPS is the usual cause.",

        _ => "Could not reach the update server. Check your connection and try again.",
    };

    private static string Name(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Nightly => "nightly",
        UpdateChannel.Preview => "preview",
        _ => "stable",
    };
}
