using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Migration;
using Kontena.Engines;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Migrate to…" modal (KON-350): recreates one container on another engine, with its
/// configuration and its named volumes' contents.
/// <para>
/// It shows the plan before it does anything — what comes along, what does not, and what stops the
/// migration entirely — because the interesting part of moving a container is what gets lost. A
/// screen that shows only ticks promises a completeness this cannot deliver.
/// </para>
/// </summary>
public sealed partial class MigrateContainerViewModel : ViewModelBase
{
    private readonly IContainerEngine _source;
    private readonly BackendRegistry _registry;
    private readonly string _containerId;
    private readonly Action _onClose;
    private readonly Func<Task> _onMigrated;

    private ContainerSummary? _summary;
    private int _composeSiblings;
    private IContainerEngine? _targetEngine;
    private MigrationPlan? _plan;

    public MigrateContainerViewModel(
        IContainerEngine source,
        BackendRegistry registry,
        string containerId,
        Action onClose,
        Func<Task> onMigrated)
    {
        _source = source;
        _registry = registry;
        _containerId = containerId;
        _onClose = onClose;
        _onMigrated = onMigrated;
    }

    /// <summary>The container being migrated, as the source engine describes it.</summary>
    public ContainerInspect Container { get; private set; } = new()
    {
        Id = string.Empty,
        Name = string.Empty,
        Image = string.Empty,
        ImageId = string.Empty,
        State = ContainerState.Unknown,
    };

    /// <summary>Engines this container could move to — every connected one except its own.</summary>
    public ObservableCollection<MigrationTargetOption> Targets { get; } = [];

    [ObservableProperty] private MigrationTargetOption? _selectedTarget;

    /// <summary>
    /// The name the migrated container gets. Editable because a name already taken on the target is a
    /// blocker with an obvious way out.
    /// </summary>
    [ObservableProperty] private string _containerName = string.Empty;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _commandPreview = string.Empty;

    /// <summary>Every line of the plan, in one list; the view groups them by kind.</summary>
    public ObservableCollection<MigrationNote> Notes { get; } = [];

    public IEnumerable<MigrationNote> Blocked =>
        Notes.Where(n => n.Kind is MigrationNoteKind.Blocked);

    public IEnumerable<MigrationNote> Dropped =>
        Notes.Where(n => n.Kind is MigrationNoteKind.Dropped);

    public IEnumerable<MigrationNote> Applied =>
        Notes.Where(n => n.Kind is MigrationNoteKind.Applied);

    /// <summary>The named volumes, with the tick that overrides "leave the target's data alone".</summary>
    public ObservableCollection<MigrationVolumeRowViewModel> Volumes { get; } = [];

    /// <summary>What the migration did, step by step, once it is running.</summary>
    public ObservableCollection<MigrationProgress> Steps { get; } = [];

    public bool HasVolumes => Volumes.Count > 0;

    public bool HasSteps => Steps.Count > 0;

    /// <summary>Loads the container, the possible targets, and a plan for the first of them.</summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            var containers = await _source.ListContainersAsync();
            _summary = containers.FirstOrDefault(c => c.Id == _containerId);
            Container = await _source.InspectContainerAsync(_containerId);
            ContainerName = Container.Name;

            // A project's services are what makes the target's lack of name resolution fatal, and
            // "is it in a project" is not enough: the last survivor of an old one is a plain
            // container. So the planner is handed the count, and this is where it is counted.
            _composeSiblings = Container.Labels.TryGetValue("com.docker.compose.project", out var project)
                ? containers.Count(c =>
                    c.Id != _containerId
                    && c.Labels.TryGetValue("com.docker.compose.project", out var other)
                    && string.Equals(other, project, StringComparison.Ordinal))
                : 0;

            await LoadTargetsAsync();
            await RefreshPlanAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadTargetsAsync()
    {
        var candidates = _registry.Providers
            .Where(p => p.Kind is BackendKind.Engine)
            .Where(p => !string.Equals(p.Backend, _summary?.Backend, StringComparison.Ordinal))
            .ToList();

        foreach (var probe in await Task.WhenAll(candidates.Select(p => BackendRegistry.ProbeAsync(p))))
        {
            // An engine that is installed but not running has nothing to receive a container: the
            // switcher lists it so you can go start it, this list would only offer a dead end.
            if (probe.Connected)
                Targets.Add(new MigrationTargetOption(probe.Provider));
        }

        SelectedTarget ??= Targets.FirstOrDefault();
    }

    partial void OnSelectedTargetChanged(MigrationTargetOption? value)
    {
        _targetEngine = null;
        _ = RefreshPlanAsync();
    }

    /// <summary>
    /// Rebuilds the plan against the selected target. Called again after a rename, so a blocker that
    /// no longer applies does not stay on screen.
    /// </summary>
    public async Task RefreshPlanAsync()
    {
        Notes.Clear();
        Volumes.Clear();
        CommandPreview = string.Empty;
        _plan = null;
        MigrateCommand.NotifyCanExecuteChanged();

        if (SelectedTarget is not { } option)
            return;

        try
        {
            _targetEngine ??= option.Provider.CreateBackend() as IContainerEngine;
            if (_targetEngine is null)
                return;

            var target = await ProbeTargetAsync(_targetEngine);

            _plan = ContainerMigrationPlanner.Plan(
                new MigrationSource(
                    Container with { Name = ContainerName.Trim() },
                    _composeSiblings),
                target);

            foreach (var note in _plan.Notes)
                Notes.Add(note);

            foreach (var volume in _plan.Volumes)
                Volumes.Add(new MigrationVolumeRowViewModel(volume, OnOverwriteChanged));

            CommandPreview = Preview(_plan.Request);
            OnPropertyChanged(nameof(HasVolumes));
            NotifyPlanChanged();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Asks the target what it already has: names in use, the networks it knows, which of this
    /// container's volumes exist and whether they hold anything, and whether the image is there.
    /// </summary>
    private async Task<MigrationTarget> ProbeTargetAsync(IContainerEngine engine)
    {
        var names = (await engine.ListContainersAsync()).Select(c => c.Name).ToList();
        var networks = (await engine.ListNetworksAsync()).Select(n => n.Name).ToList();
        var existing = (await engine.ListVolumesAsync()).Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        var volumes = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var mount in Container.Mounts.Where(m =>
            string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase)))
        {
            if (!existing.Contains(mount.Source))
                continue;

            volumes[mount.Source] = await HasDataAsync(engine, mount.Source);
        }

        return new MigrationTarget
        {
            Capabilities = engine.Capabilities,
            ContainerNames = names,
            Networks = networks,
            Volumes = volumes,
            HasImage = await engine.InspectImageAsync(Container.Image) is not null,
        };
    }

    /// <summary>
    /// Whether an existing volume on the target holds anything. <c>lost+found</c> does not count:
    /// Apple's volumes are ext4 images and every one of them carries a directory nobody created.
    /// </summary>
    private static async Task<bool> HasDataAsync(IContainerEngine engine, string volume)
    {
        // Without browsing there is no way to tell, and "assume it is full" is the answer that leaves
        // the data alone until someone ticks the box.
        if (!engine.Capabilities.SupportsVolumeBrowse)
            return true;

        var listing = await engine.BrowseVolumeAsync(volume);

        return listing.Entries.Any(e => !string.Equals(e.Name, "lost+found", StringComparison.Ordinal));
    }

    private void OnOverwriteChanged()
    {
        NotifyPlanChanged();
        OnPropertyChanged(nameof(Volumes));
    }

    private void NotifyPlanChanged()
    {
        OnPropertyChanged(nameof(Blocked));
        OnPropertyChanged(nameof(Dropped));
        OnPropertyChanged(nameof(Applied));
        OnPropertyChanged(nameof(HasBlockers));
        MigrateCommand.NotifyCanExecuteChanged();
    }

    public bool HasBlockers => _plan is not null && !_plan.CanRun;

    partial void OnIsRunningChanged(bool value) => MigrateCommand.NotifyCanExecuteChanged();

    private bool CanMigrate => _plan is { CanRun: true } && !IsRunning && !IsDone;

    [RelayCommand(CanExecute = nameof(CanMigrate))]
    private async Task MigrateAsync()
    {
        if (_plan is not { CanRun: true } plan || _targetEngine is not { } target)
            return;

        Error = null;
        IsRunning = true;
        Steps.Clear();

        try
        {
            var runner = new ContainerMigrationRunner(_source, target, StagingRoot());
            var confirmed = plan with { Volumes = [.. Volumes.Select(v => v.ToPlan())] };

            await foreach (var step in runner.RunAsync(confirmed, Container))
            {
                Steps.Add(step);
                OnPropertyChanged(nameof(HasSteps));
            }

            IsDone = true;
            await _onMigrated();
        }
        catch (Exception ex)
        {
            // Left open on purpose: the steps above it say how far the migration got, and what is on
            // the target engine now. Closing the dialog would take that away.
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>One directory per migration, named after the container it is for.</summary>
    private string StagingRoot() => Path.Combine(
        Path.GetTempPath(),
        $"kontena-migrate-{Container.Id[..Math.Min(12, Container.Id.Length)]}");

    /// <summary>
    /// The command line the plan amounts to, for someone who wants to read it before agreeing to it.
    /// <para>
    /// Built here rather than reused from the Run dialog: that one appends <c>--restart</c> in Docker's
    /// spelling to every preview, which is wrong for a target that has no restart policy at all — the
    /// exact kind of "looks right, says something untrue" this dialog exists to avoid.
    /// </para>
    /// </summary>
    private static string Preview(CreateContainerRequest request)
    {
        var line = new StringBuilder("create");

        if (request.Name is { Length: > 0 } name)
            line.Append(" --name ").Append(name);

        foreach (var port in request.Ports.Where(p => p.HostPort is not null))
            line.Append(" -p ").Append($"{port.HostPort}:{port.ContainerPort}/{port.Protocol}");

        foreach (var (key, value) in request.Environment)
            line.Append(" -e ").Append($"{key}={value}");

        foreach (var mount in request.Mounts)
        {
            line.Append(" -v ").Append($"{mount.Source}:{mount.Target}");
            if (mount.ReadOnly)
                line.Append(":ro");
        }

        foreach (var (key, value) in request.Labels)
            line.Append(" --label ").Append($"{key}={value}");

        if (request.User is { Length: > 0 } user)
            line.Append(" --user ").Append(user);

        if (request.WorkingDirectory is { Length: > 0 } directory)
            line.Append(" --workdir ").Append(directory);

        if (request.Network is { Length: > 0 } network)
            line.Append(" --network ").Append(network);

        if (request.RestartPolicy is not RestartPolicy.No)
            line.Append(" --restart ").Append(request.RestartPolicy.ToString().ToLowerInvariant());

        if (request.Entrypoint.Count > 0)
            line.Append(" --entrypoint ").Append(request.Entrypoint[0]);

        line.Append(' ').Append(request.Image);

        foreach (var part in request.Entrypoint.Skip(1).Concat(request.Command))
            line.Append(' ').Append(part.Contains(' ', StringComparison.Ordinal) ? $"\"{part}\"" : part);

        return line.ToString();
    }

    [RelayCommand]
    private async Task RenameAsync() => await RefreshPlanAsync();

    [RelayCommand]
    private void Cancel() => _onClose();
}

/// <summary>One engine this container could be migrated to.</summary>
public sealed class MigrationTargetOption(IBackendProvider provider)
{
    public IBackendProvider Provider { get; } = provider;

    public string Backend => Provider.Backend;

    public string DisplayName => Provider.DisplayName;

    public override string ToString() => DisplayName;
}

/// <summary>One volume row, with the tick that says "yes, overwrite what is there".</summary>
public sealed partial class MigrationVolumeRowViewModel : ViewModelBase
{
    private readonly VolumePlan _plan;
    private readonly Action _onChanged;

    public MigrationVolumeRowViewModel(VolumePlan plan, Action onChanged)
    {
        _plan = plan;
        _onChanged = onChanged;
        _overwrite = plan.Overwrite;
    }

    public string Name => _plan.Name;

    /// <summary>True only when the target already holds data — the one case that needs a decision.</summary>
    public bool NeedsDecision => _plan.ExistsOnTarget && _plan.TargetHasData;

    [ObservableProperty] private bool _overwrite;

    public string Status => ToPlan().WillCopy
        ? "Contents will be copied"
        : "Exists on the target and holds data — left alone";

    partial void OnOverwriteChanged(bool value)
    {
        OnPropertyChanged(nameof(Status));
        _onChanged();
    }

    public VolumePlan ToPlan() => _plan with { Overwrite = Overwrite };
}
