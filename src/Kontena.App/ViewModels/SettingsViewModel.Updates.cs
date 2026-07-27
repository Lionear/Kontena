using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Settings › Updates (KON-110): channel, auto-download and what the updater is doing.
/// </summary>
public partial class SettingsViewModel
{
    // ── Updates (KON-110) ───────────────────────────────────────────────────

    /// <summary>The updater, so the category can show its state and trigger a check. Null in tests.</summary>
    public UpdateViewModel? Update { get; }

    /// <summary>Whether the category is offered at all — it is meaningless without an updater.</summary>
    public bool HasUpdates => Update is not null;

    /// <summary>
    /// Whether this install can replace itself. False for a distro package or an unpacked archive:
    /// the channel and auto-download rows would then promise something that cannot happen.
    /// </summary>
    public bool CanSelfUpdate => Update?.CanSelfUpdate ?? false;

    /// <summary>
    /// The two halves of the category, as named properties rather than a binding-side negation:
    /// they must never both be on screen, and one expression that can silently fail to evaluate is
    /// exactly how they end up contradicting each other.
    /// </summary>
    public bool ShowUpdatePreferences => CanSelfUpdate;

    public bool ShowUnsupportedNotice => HasUpdates && !CanSelfUpdate;

    [ObservableProperty] private UpdateChannel _updateChannel;

    private readonly UpdateChannel _buildChannel;

    /// <summary>False until the user picks a channel; drives the "following this build" note.</summary>
    private bool _channelWasChosen;

    /// <summary>
    /// Shown while the channel is only being followed rather than chosen, and only when that means
    /// something — on a stable build "following the build" and "stable" are the same sentence.
    /// </summary>
    public bool IsFollowingBuildChannel => !_channelWasChosen && _buildChannel != UpdateChannel.Stable;

    public string FollowingBuildNote =>
        $"This is a {ReleaseChannel.Stream(_buildChannel)} build, so Kontena is following that channel. "
        + "Pick one to decide for yourself.";

    public bool IsStableChannel => UpdateChannel == UpdateChannel.Stable;
    public bool IsPreviewChannel => UpdateChannel == UpdateChannel.Preview;
    public bool IsNightlyChannel => UpdateChannel == UpdateChannel.Nightly;

    partial void OnUpdateChannelChanged(UpdateChannel value)
    {
        // Touching the control is the choice, even when it lands on the value already shown.
        _channelWasChosen = true;
        OnPropertyChanged(nameof(IsFollowingBuildChannel));
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPreviewChannel));
        OnPropertyChanged(nameof(IsNightlyChannel));
        OnPropertyChanged(nameof(ChannelHint));
        Save();

        // The channel decides which feed is read, so what was found on the old one no longer
        // applies — ask again rather than leave a stale offer on screen.
        _ = Update?.CheckAsync();
    }

    /// <summary>What the chosen channel means, in terms of how finished the builds on it are.</summary>
    public string ChannelHint => UpdateChannel switch
    {
        UpdateChannel.Nightly =>
            "Cut from develop every night: everything that is finished, and whatever came with it. "
            + "The first place a regression shows up.",
        UpdateChannel.Preview =>
            "Built from main — what has been promoted for the next release, before it is tagged. "
            + "Ahead of stable, past the roughest edges of nightly.",
        _ => "Tagged releases only. This is the one to be on unless you are testing Kontena itself.",
    };

    [RelayCommand]
    private void SetUpdateChannel(string channel) => UpdateChannel = channel switch
    {
        "nightly" => UpdateChannel.Nightly,
        "preview" => UpdateChannel.Preview,
        _ => UpdateChannel.Stable,
    };

    [ObservableProperty] private bool _autoDownloadUpdates;
    partial void OnAutoDownloadUpdatesChanged(bool value) => Save();

    /// <summary>Check now — the manual counterpart of the check on launch.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (Update is not null)
            await Update.CheckAsync(userAsked: true);
    }
}
