using Kontena.Adapters.Podman;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Engines;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Kontena.Core.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The one case Kontena offers a checked fix for: Podman installed, its user socket unit present but
/// never enabled — the reason <c>podman ps</c> works from a terminal while Kontena finds nothing.
/// Linux-only, since it is systemd's own state being read.
/// </summary>
public sealed class PodmanSocketFixTests
{
    private static readonly ExternalTool Systemctl = new("systemctl", "systemctl", ["--version"], []);

    [SkippableFact]
    public async Task An_inactive_unit_is_fixable()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var runner = new FakeToolRunner()
            .Install(Systemctl)
            .When(i => i.Arguments.Contains("is-active"), output: ["inactive"], exitCode: 3);

        Assert.True(await PodmanSocketFix.IsFixableAsync(runner));
    }

    [SkippableFact]
    public async Task An_already_active_unit_is_not_offered_as_a_fix()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var runner = new FakeToolRunner()
            .Install(Systemctl)
            .When(i => i.Arguments.Contains("is-active"), output: ["active"], exitCode: 0);

        Assert.False(await PodmanSocketFix.IsFixableAsync(runner));
    }

    [SkippableFact]
    public async Task No_systemctl_on_the_machine_means_no_fix_to_offer()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var runner = new FakeToolRunner();

        Assert.False(await PodmanSocketFix.IsFixableAsync(runner));
    }

    [Fact]
    public void The_suggested_command_needs_no_elevation()
    {
        // A --user unit — sudo would manage the wrong (system-wide) one instead.
        Assert.Equal("--user", PodmanSocketFix.EnableSocket.Arguments[0]);
        Assert.Equal("systemctl --user enable --now podman.socket", PodmanSocketFix.EnableSocket.CommandLine);
    }

    /// <summary>
    /// End to end through the shell: an unreachable Podman probe plus an inactive unit surfaces the
    /// command on the down card, without anyone having to click Reconnect first.
    /// </summary>
    [SkippableFact]
    public async Task The_down_card_offers_the_fix_when_podman_is_unreachable_and_the_unit_is_inactive()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var path = Path.Combine(Path.GetTempPath(), $"kontena-podmanfix-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new KontenaSettings { Onboarded = true };
            store.Save(settings);

            var runner = new FakeToolRunner()
                .Install(Systemctl)
                .When(i => i.Arguments.Contains("is-active"), output: ["inactive"], exitCode: 3);

            var vm = new MainWindowViewModel(
                new BackendRegistry([new UnreachablePodmanProvider()]), store, settings,
                new FakeUpdateService(), runner);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.BackendDownFixCommand is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal("systemctl --user enable --now podman.socket", vm.BackendDownFixCommand);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// The same fix on the first-run wizard (KON-335). It belongs there more than on the down card:
    /// the wizard is the screen that asks you to start an engine, and it used to be the screen with no
    /// way to do it.
    /// </summary>
    [SkippableFact]
    public async Task The_wizard_offers_the_fix_for_an_engine_it_reports_as_not_running()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var (vm, cleanup) = await WizardWithInactivePodmanAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.Onboarding?.FixCommandLine is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal("systemctl --user enable --now podman.socket", vm.Onboarding!.FixCommandLine);
        }
        finally { cleanup(); }
    }

    /// <summary>
    /// A fix that fails says so and leaves the screen where it was. Rescanning on failure would redraw
    /// the same stopped engine, which reads as if nothing had been tried.
    /// </summary>
    [SkippableFact]
    public async Task A_failed_start_is_reported_and_keeps_the_wizard_up()
    {
        Skip.If(!OperatingSystem.IsLinux(), "systemd/Linux only.");

        var (vm, cleanup) = await WizardWithInactivePodmanAsync(enableFails: true);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.Onboarding?.FixCommandLine is null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            var wizard = vm.Onboarding!;
            await wizard.StartEngineCommand.ExecuteAsync(null);

            Assert.NotNull(wizard.FixError);
            Assert.True(vm.IsOnboarding);
            Assert.Same(wizard, vm.Onboarding);
        }
        finally { cleanup(); }
    }

    /// <summary>A first run whose only engine is an unreachable Podman with an inactive socket unit.</summary>
    private static async Task<(MainWindowViewModel Vm, Action Cleanup)> WizardWithInactivePodmanAsync(
        bool enableFails = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-wizardfix-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        var settings = new KontenaSettings { Onboarded = false };
        store.Save(settings);

        var runner = new FakeToolRunner()
            .Install(Systemctl)
            .When(i => i.Arguments.Contains("is-active"), output: ["inactive"], exitCode: 3)
            .When(i => i.Arguments.Contains("enable"),
                output: enableFails ? ["Failed to connect to bus."] : [],
                exitCode: enableFails ? 1 : 0);

        var vm = new MainWindowViewModel(
            new BackendRegistry([new UnreachablePodmanProvider()]), store, settings,
            new FakeUpdateService(), runner);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.IsOnboarding && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsOnboarding, "the shell never showed the onboarding wizard");

        return (vm, () => { if (File.Exists(path)) File.Delete(path); });
    }

    /// <summary>A provider that always fails to connect, under the "podman" id — the shortest route
    /// to an unreachable-Podman probe without a real socket.</summary>
    private sealed class UnreachablePodmanProvider : IBackendProvider
    {
        public string Backend => "podman";
        public string DisplayName => "Podman";
        public string Chip => "P";
        public BackendChipStyle? ChipStyle => null;
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new UnreachableBackend();

        private sealed class UnreachableBackend : IBackend
        {
            public string Backend => "podman";
            public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
                => throw new InvalidOperationException("unreachable");
            public ValueTask PingAsync(CancellationToken ct = default)
                => throw new InvalidOperationException("unreachable");
        }
    }
}
