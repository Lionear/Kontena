using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

public class AlertDetailViewModelTests
{
    private static Alert Alert(IReadOnlyDictionary<string, string>? annotations = null) => new()
    {
        Labels = new Dictionary<string, string>
        {
            ["alertname"] = "KubePodCrashLooping",
            ["severity"] = "critical",
            ["namespace"] = "app",
            ["pod"] = "redis-7d9c4f-x2ktp",
        },
        Annotations = annotations ?? new Dictionary<string, string>(),
        StartsAt = DateTimeOffset.UtcNow.AddHours(-1),
    };

    [Fact]
    public void Silence_matchers_cover_every_label_but_severity()
    {
        // severity is routing metadata, not identity — matching on it would either silence too
        // broadly or too narrowly depending on what the routing config makes of the word.
        var matchers = AlertDetailViewModel.MatchersFor(Alert());

        Assert.DoesNotContain(matchers, m => m.Name == "severity");
        Assert.Contains(matchers, m => m.Name == "alertname" && m.Value == "KubePodCrashLooping");
        Assert.Contains(matchers, m => m.Name == "namespace" && m.Value == "app");
        Assert.Contains(matchers, m => m.Name == "pod" && m.Value == "redis-7d9c4f-x2ktp");
        Assert.Equal(3, matchers.Count);
    }

    [Fact]
    public void Silence_always_carries_the_default_expiry()
    {
        SilenceRequest? sent = null;
        var detail = new AlertDetailViewModel(Alert(), rule: null, silence: null, onSilence: r => sent = r);

        detail.SilenceCommand.Execute(null);

        Assert.NotNull(sent);
        // Never open-ended (KON-204 §5): a silence without an end is a rule deleted without saying so.
        Assert.Equal(AlertDetailViewModel.DefaultSilenceDuration, sent!.EndsAt - sent.StartsAt);
        Assert.Equal(3, sent.Matchers.Count);
    }

    [Fact]
    public void Without_a_silence_callback_the_command_is_a_no_op_not_a_silent_mute()
    {
        var detail = new AlertDetailViewModel(Alert(), rule: null, silence: null);
        Assert.False(detail.CanCreateSilence);
        // Executes without throwing — the drawer must never claim a silence happened when nobody wired it up.
        detail.SilenceCommand.Execute(null);
    }

    [Fact]
    public void Already_silenced_offers_expire_instead_of_silence()
    {
        var silence = new Silence
        {
            Id = "sil-1",
            Matchers = [],
            CreatedBy = "rick",
            Comment = "known",
            EndsAt = DateTimeOffset.UtcNow.AddHours(2),
        };

        Silence? expired = null;
        var detail = new AlertDetailViewModel(Alert(), rule: null, silence, onExpireSilence: s => expired = s);

        Assert.False(detail.CanCreateSilence);
        Assert.True(detail.CanExpireSilence);
        Assert.Contains("rick", detail.SilenceStatusText, StringComparison.Ordinal);

        detail.ExpireSilenceCommand.Execute(null);
        Assert.Same(silence, expired);
    }

    [Fact]
    public void Footer_jumps_are_gated_on_what_the_alert_actually_carries()
    {
        // No namespace/pod labels, no runbook_url, no generatorURL — every jump should say no rather
        // than open to nothing.
        var bare = new Alert { Labels = new Dictionary<string, string> { ["alertname"] = "X" } };
        var detail = new AlertDetailViewModel(bare, rule: null, silence: null, onOpenPod: (_, _) => Task.FromResult(true));

        Assert.False(detail.CanOpenPod);
        Assert.False(detail.CanOpenLogs);
        Assert.False(detail.CanOpenRunbook);
        Assert.False(detail.CanOpenGraph);
    }

    [Fact]
    public async Task Pod_and_Logs_jump_to_the_same_target_on_different_tabs()
    {
        var calls = new List<(ResourceRef Target, string Tab)>();
        var detail = new AlertDetailViewModel(
            Alert(), rule: null, silence: null,
            onOpenPod: (target, tab) => { calls.Add((target, tab)); return Task.FromResult(true); });

        Assert.True(detail.CanOpenPod);
        Assert.True(detail.CanOpenLogs);

        await detail.OpenPodCommand.ExecuteAsync(null);
        await detail.OpenLogsCommand.ExecuteAsync(null);

        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Target, calls[1].Target);
        Assert.Equal("overview", calls[0].Tab);
        Assert.Equal("logs", calls[1].Tab);
        Assert.Equal("app", calls[0].Target.Namespace);
        Assert.Equal("redis-7d9c4f-x2ktp", calls[0].Target.Name);
    }
}
