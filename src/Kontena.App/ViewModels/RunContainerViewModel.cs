using System.Collections.ObjectModel;
using System.Text;
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
public partial class RunContainerViewModel : ViewModelBase
{
    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Func<Task> _onCreated;
    private readonly IReadOnlySet<string> _localImages;

    public RunContainerViewModel(
        IContainerEngine engine,
        string backendName,
        string backendChip,
        IReadOnlyList<string> networks,
        IReadOnlySet<string> localImages,
        Action onClose,
        Func<Task> onCreated)
    {
        _engine = engine;
        _onClose = onClose;
        _onCreated = onCreated;
        _localImages = localImages;

        BackendName = backendName;
        BackendChip = backendChip;

        Networks = ["(default)", .. networks];
        SelectedNetwork = "(default)";

        RestartPolicies = ["no", "on-failure", "unless-stopped", "always"];
        SelectedRestartPolicy = "no";

        UpdatePreview();
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

    /// <summary>True when the entered image is already present locally.</summary>
    public bool IsImageLocal => !string.IsNullOrWhiteSpace(Image) && _localImages.Contains(Image.Trim());
    public bool IsImagePulled => !string.IsNullOrWhiteSpace(Image) && !IsImageLocal;

    public bool CanRun => !string.IsNullOrWhiteSpace(Image) && !IsBusy;

    partial void OnImageChanged(string value)
    {
        OnPropertyChanged(nameof(IsImageLocal));
        OnPropertyChanged(nameof(IsImagePulled));
        OnPropertyChanged(nameof(CanRun));
        UpdatePreview();
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
