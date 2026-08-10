using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Everything that opens in the modal slot — Run, Pull, Build, Compose, volumes, networks,
/// confirmations and the add-backend wizard. One place decides how something is asked.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Put a confirmation in front of a page's action (KON-126). Pages never build the modal themselves
    /// — they describe what they are about to do, and this is the one place that decides how it is asked.
    /// </summary>
    private void ShowConfirm(ConfirmRequest request)
        => Dialog = new ConfirmViewModel(
            request.Title,
            request.Message,
            request.ConfirmLabel,
            onConfirm: async () =>
            {
                await request.OnConfirm();
                CloseDialog();
            },
            onClose: CloseDialog,
            destructive: request.Destructive,
            details: request.Details);
    private void ShowNetworkAttachmentsDialog(NetworkSummary network)
    {
        if (_engine is null)
            return;

        Dialog = new NetworkAttachmentsViewModel(_engine, network, CloseDialog, onChanged: async () =>
        {
            if (Networks is not null)
                await Networks.LoadAsync();
        });
    }
    private void ShowCreateNetworkDialog()
    {
        if (_engine is null)
            return;

        Dialog = new CreateNetworkViewModel(_engine, CloseDialog, onCreated: async () =>
        {
            if (Networks is not null)
                await Networks.LoadAsync();
            await UpdateNavCountsAsync();
        });
    }
    [RelayCommand]
    private void ShowBrowseVolumeDialog(string volume)
    {
        if (_engine is null)
            return;

        Dialog = new BrowseVolumeViewModel(_engine, volume, CloseDialog);
    }
    private void ShowCreateVolumeDialog()
    {
        if (_engine is null)
            return;

        Dialog = new CreateVolumeViewModel(_engine, CloseDialog, onCreated: async () =>
        {
            if (Volumes is not null)
                await Volumes.LoadAsync();
            await UpdateNavCountsAsync();
        });
    }
    /// <summary>
    /// The switcher's "Add engine or cluster…" row (KON-118). Opens the wizard, which ends in a
    /// connection that has actually been made — the reason it is a wizard and not a form.
    /// </summary>
    [RelayCommand]
    private void ShowAddBackend() => ShowAddBackend(AddBackendStep.What);
    private void ShowAddBackend(AddBackendStep start)
    {
        Dialog = new AddBackendViewModel(_store, _probes, CloseDialog, async backend =>
        {
            await ReloadBackendsAsync(BackendCatalog.ShouldIncludeDemo(_settings.ShowDemoBackends));

            // Switch to what was just added, but only if it is really there: a rebuild can drop a
            // provider whose configuration turned out to be unusable.
            if (backend is { Length: > 0 }
                && _registry.Providers.FirstOrDefault(p => p.Backend == backend) is { } provider)
            {
                await ActivateAsync(provider);
            }
        }, start);
    }
    /// <summary>
    /// "Migrate to…" on a container row (KON-350). The dialog does its own reading — the plan needs
    /// both engines — so this only hands it what it needs to find them.
    /// </summary>
    private async Task ShowMigrateDialogAsync(string containerId)
    {
        if (_engine is null)
            return;

        var model = new MigrateContainerViewModel(
            _engine, _registry, containerId,
            onClose: CloseDialog,
            onMigrated: async () =>
            {
                if (Containers is not null)
                    await Containers.LoadAsync();
            });

        Dialog = model;
        await model.InitializeAsync();
    }

    private async Task ShowRunDialogAsync(string? initialImage = null)
    {
        if (_engine is null)
            return;

        var networks = (await _engine.ListNetworksAsync()).Select(n => n.Name).ToList();
        var images = (await _engine.ListImagesAsync())
            .Select(i => $"{i.Repository}:{i.Tag}")
            .ToHashSet(StringComparer.Ordinal);

        Dialog = new RunContainerViewModel(
            _engine, EngineName, EngineChip, networks, images,
            onClose: CloseDialog,
            onCreated: async () =>
            {
                if (Containers is not null)
                    await Containers.LoadAsync();
            },
            initialImage: initialImage,
            credentials: _registryCredentials);
    }
    private void ShowPullDialog()
    {
        if (_engine is null)
            return;

        Dialog = new PullImageViewModel(
            _engine, CloseDialog, onPulled: RefreshAfterPullAsync, credentials: _registryCredentials);
    }
    private async Task RefreshAfterPullAsync()
    {
        if (Images is { HasLoaded: true })
            await Images.LoadAsync();
        await UpdateNavCountsAsync();
    }
    private void ShowBuildDialog()
    {
        if (_engine is null)
            return;

        Dialog = new BuildImageViewModel(_engine, CloseDialog,
            onRun: image =>
            {
                CloseDialog();
                _ = ShowRunDialogAsync(image);
            },
            recentContexts: _settings.RecentBuildContexts,
            onContextUsed: RecordRecentContext);
    }
    /// <summary>Remember a just-used build context, most-recent first, capped to a short list.</summary>
    private void RecordRecentContext(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _settings = _store.Update(s =>
        {
            var recent = new List<string> { path };
            recent.AddRange(s.RecentBuildContexts
                .Where(c => !string.Equals(c, path, StringComparison.Ordinal)));

            return s with { RecentBuildContexts = recent.Take(6).ToList() };
        });
    }
    private void ShowComposeUpDialog()
    {
        if (_engine is null)
            return;

        Dialog = new ComposeUpViewModel(_engine, CloseDialog, onUp: RefreshComposeAsync);
    }
    private void ShowComposeLogsDialog(ComposeProjectViewModel project)
    {
        if (_engine is null)
            return;

        Dialog = new ComposeLogsViewModel(_engine, project.Name, project.LogSources, CloseDialog);
    }
    private async Task RefreshComposeAsync()
    {
        if (ComposeProjects is { HasLoaded: true })
            await ComposeProjects.LoadAsync();
        await UpdateNavCountsAsync();
    }
    private void CloseDialog()
    {
        (Dialog as IDisposable)?.Dispose();
        Dialog = null;
    }
}
