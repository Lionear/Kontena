using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Run a container" modal: collects an image + options, shows a live
/// engine-flavoured command preview, and creates the container via the CEAL
/// (which auto-pulls a missing image). Hosted as an overlay by the shell.
/// </summary>
public partial class RunContainerViewModel : ViewModelBase, IDisposable
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onCreated;
    private readonly HashSet<string> _localImages;

    public RunContainerViewModel(
        IContainerEngine engine,
        string backendName,
        string backendChip,
        IReadOnlyList<string> networks,
        IReadOnlySet<string> localImages,
        Action onClose,
        Func<Task> onCreated,
        string? initialImage = null)
    {
        _engine = engine;
        _onClose = onClose;
        _onCreated = onCreated;
        _localImages = new HashSet<string>(localImages, StringComparer.Ordinal);

        BackendName = backendName;
        BackendChip = backendChip;

        Networks = ["(default)", .. networks];
        SelectedNetwork = "(default)";

        RestartPolicies = ["no", "on-failure", "unless-stopped", "always"];
        SelectedRestartPolicy = "no";

        UpdatePreview();

        // Setting via the property (not the field) fires the pre-fill for the image.
        if (!string.IsNullOrWhiteSpace(initialImage))
            Image = initialImage;
    }

    public string BackendName { get; }
    public string BackendChip { get; }

    public ObservableCollection<string> Networks { get; }
    public string[] RestartPolicies { get; }

    [ObservableProperty] private string _image = string.Empty;
    [ObservableProperty] private string _containerName = string.Empty;
    [ObservableProperty] private string _selectedNetwork = "(default)";
    [ObservableProperty] private string _selectedRestartPolicy = "no";

    [ObservableProperty] private string _commandPreview = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<PortRow> Ports { get; } = [];
    public ObservableCollection<EnvRow> EnvVars { get; } = [];
    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    [ObservableProperty] private bool _isPulling;
    [ObservableProperty] private string _pullStatus = string.Empty;

    /// <summary>True when the entered image is already present locally.</summary>
    public bool IsImageLocal => !string.IsNullOrWhiteSpace(Image) && _localImages.Contains(Image.Trim());
    public bool IsImagePulled => !string.IsNullOrWhiteSpace(Image) && !IsImageLocal;

    /// <summary>Pull is offered when an image is entered that isn't local yet.</summary>
    public bool CanPull => IsImagePulled && !IsPulling;

    /// <summary>Show the "why pull now" hint while a not-yet-local image is entered.</summary>
    public bool ShowPullHint => IsImagePulled && !IsPulling;

    public bool CanRun => !string.IsNullOrWhiteSpace(Image) && !IsBusy && !IsPulling;

    partial void OnImageChanged(string value)
    {
        OnPropertyChanged(nameof(IsImageLocal));
        OnPropertyChanged(nameof(IsImagePulled));
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(ShowPullHint));
        OnPropertyChanged(nameof(CanRun));
        UpdatePreview();
        SchedulePrefill();
    }

    partial void OnIsPullingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(ShowPullHint));
        OnPropertyChanged(nameof(CanRun));
    }

    /// <summary>Pull the entered image, then scaffold its ports/volumes from the fresh metadata.</summary>
    [RelayCommand]
    private async Task PullAsync()
    {
        var reference = Image.Trim();
        if (string.IsNullOrWhiteSpace(reference) || IsPulling)
            return;

        Error = null;
        IsPulling = true;
        PullStatus = "Preparing…";
        try
        {
            await foreach (var progress in _engine.PullImageAsync(reference))
                PullStatus = FormatPull(progress);

            _localImages.Add(reference);
            OnPropertyChanged(nameof(IsImageLocal));
            OnPropertyChanged(nameof(IsImagePulled));
            OnPropertyChanged(nameof(CanPull));
            OnPropertyChanged(nameof(ShowPullHint));
            PullStatus = "Pulled ✓";

            await ScaffoldFromImageAsync(reference, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            PullStatus = string.Empty;
        }
        finally
        {
            IsPulling = false;
        }
    }

    private static string FormatPull(PullProgress progress)
    {
        if (progress.Total is > 0 && progress.Current is >= 0)
            return $"{progress.Status} {(int)(100.0 * progress.Current.Value / progress.Total.Value)}%";

        return progress.Status;
    }

    // ── Pre-fill from image metadata ────────────────────────────────────────────

    private CancellationTokenSource? _prefillCts;

    /// <summary>
    /// A short debounce after typing: if the image is present locally, inspect it
    /// and scaffold its exposed ports and declared volume mounts — but only while
    /// the user hasn't started filling those in themselves.
    /// </summary>
    private void SchedulePrefill()
    {
        _prefillCts?.Cancel();
        _prefillCts = new CancellationTokenSource();
        _ = PrefillAsync(Image.Trim(), _prefillCts.Token);
    }

    private async Task PrefillAsync(string reference, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);

            if (string.IsNullOrWhiteSpace(reference) || !_localImages.Contains(reference))
                return;

            await ScaffoldFromImageAsync(reference, ct);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        catch
        {
            // pre-fill is best-effort — never block the modal on it
        }
    }

    private async Task ScaffoldFromImageAsync(string reference, CancellationToken ct)
    {
        var config = await _engine.InspectImageAsync(reference, ct);
        if (config is null || ct.IsCancellationRequested)
            return;

        if (Ports.Count == 0 && config.ExposedPorts.Count > 0)
        {
            foreach (var port in config.ExposedPorts)
            {
                Ports.Add(new PortRow(UpdatePreview)
                {
                    Host = port.ContainerPort.ToString(CultureInfo.InvariantCulture),
                    Container = $"{port.ContainerPort}/{port.Protocol}",
                });
            }
        }

        if (Volumes.Count == 0 && config.Volumes.Count > 0)
        {
            foreach (var destination in config.Volumes)
            {
                Volumes.Add(new VolumeRow(UpdatePreview)
                {
                    Source = SuggestVolumeName(reference, destination),
                    Destination = destination,
                });
            }
        }

        UpdatePreview();
    }

    /// <summary>Suggest a volume name like "postgres-data" from the image + mount point.</summary>
    private static string SuggestVolumeName(string reference, string destination)
    {
        var repo = reference.Split(':')[0];
        var name = repo.Contains('/') ? repo[(repo.LastIndexOf('/') + 1)..] : repo;

        var leaf = destination.Trim('/');
        if (leaf.Contains('/'))
            leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        return string.IsNullOrEmpty(leaf) ? $"{name}-data" : $"{name}-{leaf}";
    }

    partial void OnContainerNameChanged(string value) => UpdatePreview();
    partial void OnSelectedNetworkChanged(string value) => UpdatePreview();
    partial void OnSelectedRestartPolicyChanged(string value) => UpdatePreview();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    // ── Repeaters ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddPort()
    {
        Ports.Add(new PortRow(UpdatePreview));
        UpdatePreview();
    }

    [RelayCommand]
    private void RemovePort(PortRow row)
    {
        Ports.Remove(row);
        UpdatePreview();
    }

    [RelayCommand]
    private void AddEnv()
    {
        EnvVars.Add(new EnvRow(UpdatePreview));
        UpdatePreview();
    }

    [RelayCommand]
    private void RemoveEnv(EnvRow row)
    {
        EnvVars.Remove(row);
        UpdatePreview();
    }

    [RelayCommand]
    private void AddVolume()
    {
        Volumes.Add(new VolumeRow(UpdatePreview));
        UpdatePreview();
    }

    [RelayCommand]
    private void RemoveVolume(VolumeRow row)
    {
        Volumes.Remove(row);
        UpdatePreview();
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    private void UpdatePreview()
    {
        var sb = new StringBuilder();
        sb.Append(BackendName.ToLowerInvariant()).Append(" run -d");

        if (!string.IsNullOrWhiteSpace(ContainerName))
            sb.Append(" --name ").Append(ContainerName.Trim());

        foreach (var p in Ports)
        {
            if (string.IsNullOrWhiteSpace(p.Container))
                continue;
            sb.Append(" -p ");
            if (!string.IsNullOrWhiteSpace(p.Host))
                sb.Append(p.Host.Trim()).Append(':');
            sb.Append(p.Container.Trim());
        }

        foreach (var e in EnvVars)
        {
            if (string.IsNullOrWhiteSpace(e.Key))
                continue;
            sb.Append(" -e ").Append(e.Key.Trim()).Append('=').Append(e.Value.Trim());
        }

        foreach (var v in Volumes)
        {
            if (string.IsNullOrWhiteSpace(v.Source) || string.IsNullOrWhiteSpace(v.Destination))
                continue;
            sb.Append(" -v ").Append(v.Source.Trim()).Append(':').Append(v.Destination.Trim());
        }

        if (SelectedNetwork is not ("(default)" or null) && SelectedNetwork.Length > 0)
            sb.Append(" --network ").Append(SelectedNetwork);

        if (SelectedRestartPolicy is not "no")
            sb.Append(" --restart ").Append(SelectedRestartPolicy);

        sb.Append(' ').Append(string.IsNullOrWhiteSpace(Image) ? "<image>" : Image.Trim());

        CommandPreview = sb.ToString();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Cancel() => _onClose();

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanRun)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            var ports = new List<PortBinding>();
            foreach (var p in Ports)
            {
                if (!TryParsePort(p.Container, out var containerPort, out var protocol))
                    continue;
                int? host = int.TryParse(p.Host?.Trim(), out var h) ? h : null;
                ports.Add(new PortBinding(host, containerPort, protocol));
            }

            var env = new Dictionary<string, string>();
            foreach (var e in EnvVars)
            {
                if (!string.IsNullOrWhiteSpace(e.Key))
                    env[e.Key.Trim()] = e.Value.Trim();
            }

            var volumes = new Dictionary<string, string>();
            foreach (var v in Volumes)
            {
                if (!string.IsNullOrWhiteSpace(v.Source) && !string.IsNullOrWhiteSpace(v.Destination))
                    volumes[v.Source.Trim()] = v.Destination.Trim();
            }

            var request = new CreateContainerRequest
            {
                Image = Image.Trim(),
                Name = string.IsNullOrWhiteSpace(ContainerName) ? null : ContainerName.Trim(),
                Ports = ports,
                Environment = env,
                Volumes = volumes,
                Network = SelectedNetwork is "(default)" ? null : SelectedNetwork,
                RestartPolicy = ParseRestart(SelectedRestartPolicy),
                Start = true,
            };

            await _engine.CreateContainerAsync(request);
            await _onCreated();
            _onClose();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Parse "5432" or "5432/tcp" into a port and protocol.</summary>
    private static bool TryParsePort(string? text, out int port, out string protocol)
    {
        port = 0;
        protocol = "tcp";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            protocol = value[(slash + 1)..].Trim().ToLowerInvariant() is { Length: > 0 } p ? p : "tcp";
            value = value[..slash];
        }

        return int.TryParse(value.Trim(), out port);
    }

    private static RestartPolicy ParseRestart(string policy) => policy switch
    {
        "always" => RestartPolicy.Always,
        "on-failure" => RestartPolicy.OnFailure,
        "unless-stopped" => RestartPolicy.UnlessStopped,
        _ => RestartPolicy.No,
    };

    public void Dispose()
    {
        _prefillCts?.Cancel();
        _prefillCts?.Dispose();
        _prefillCts = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A host→container port row in the Run modal.</summary>
public partial class PortRow(Action changed) : ObservableObject
{
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _container = string.Empty;

    partial void OnHostChanged(string value) => changed();
    partial void OnContainerChanged(string value) => changed();
}

/// <summary>A KEY=value environment row in the Run modal.</summary>
public partial class EnvRow(Action changed) : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;

    partial void OnKeyChanged(string value) => changed();
    partial void OnValueChanged(string value) => changed();
}

/// <summary>A source→destination volume mount row in the Run modal.</summary>
public partial class VolumeRow(Action changed) : ObservableObject
{
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _destination = string.Empty;

    partial void OnSourceChanged(string value) => changed();
    partial void OnDestinationChanged(string value) => changed();
}
