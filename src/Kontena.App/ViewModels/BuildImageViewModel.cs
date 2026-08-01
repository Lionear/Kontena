using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Build image" modal: collects a Dockerfile build (context, tag, target,
/// options, args), then streams the builder's output over the CEAL into a parsed
/// step list, a console, and a progress bar. On success it can open the Run modal
/// pre-filled with the freshly built tag.
/// </summary>
public partial class BuildImageViewModel : ViewModelBase, IDisposable
{
    private const int MaxConsoleLines = 2000;

    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly Action<string> _onRun;
    private readonly Action<string>? _onContextUsed;
    private readonly Stopwatch _elapsed = new();

    private CancellationTokenSource? _cts;
    private BuildStepViewModel? _current;
    private bool _cacheHit;

    public BuildImageViewModel(
        IContainerEngine engine,
        Action onClose,
        Action<string> onRun,
        IReadOnlyList<string>? recentContexts = null,
        Action<string>? onContextUsed = null)
    {
        _engine = engine;
        _onClose = onClose;
        _onRun = onRun;
        _onContextUsed = onContextUsed;

        if (recentContexts is not null)
            foreach (var path in recentContexts)
                RecentContexts.Add(path);
    }

    /// <summary>Recently used build-context folders, offered as quick-picks.</summary>
    public ObservableCollection<string> RecentContexts { get; } = [];

    public bool HasRecentContexts => RecentContexts.Count > 0;

    [RelayCommand]
    private void UseRecentContext(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            ContextPath = path;
    }

    // ── Config ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _contextPath = string.Empty;
    [ObservableProperty] private string _dockerfile = "Dockerfile";
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private bool _noCache;
    [ObservableProperty] private bool _pull = true;

    public ObservableCollection<BuildArgRow> BuildArgs { get; } = [];

    partial void OnContextPathChanged(string value) => OnPropertyChanged(nameof(CanBuild));
    partial void OnTagChanged(string value) => OnPropertyChanged(nameof(CanBuild));
    partial void OnDockerfileChanged(string value) => OnPropertyChanged(nameof(CanBuild));

    /// <summary>Called after picking a Dockerfile: default the context to its folder (overridable).</summary>
    public void SetDockerfile(string path)
    {
        Dockerfile = path;
        if (string.IsNullOrWhiteSpace(ContextPath))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                ContextPath = dir;
        }
    }

    [RelayCommand]
    private void AddArg() => BuildArgs.Add(new BuildArgRow());

    [RelayCommand]
    private void RemoveArg(BuildArgRow row) => BuildArgs.Remove(row);

    // ── State ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;

    public bool NotStarted => !IsBuilding && !IsDone && !IsFailed;
    public bool HasOutput => IsBuilding || IsDone || IsFailed;
    public bool CanBuild => !string.IsNullOrWhiteSpace(ContextPath) && !string.IsNullOrWhiteSpace(Tag) && NotStarted;

    partial void OnIsBuildingChanged(bool value) => RaiseState();
    partial void OnIsDoneChanged(bool value) => RaiseState();
    partial void OnIsFailedChanged(bool value) => RaiseState();

    private void RaiseState()
    {
        OnPropertyChanged(nameof(NotStarted));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(CanBuild));
    }

    public ObservableCollection<BuildStepViewModel> Steps { get; } = [];
    public ObservableCollection<string> Console { get; } = [];

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusLine = string.Empty;
    [ObservableProperty] private string _elapsedText = "0s";

    // ── Build ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (!CanBuild)
            return;

        var context = ContextPath.Trim();
        if (!Directory.Exists(context))
        {
            Fail($"Build context not found: {context}");
            return;
        }

        // The Dockerfile may be absolute (picked) or relative to the context; the /build
        // endpoint needs it as a path relative to (and inside) the context tar.
        var dfInput = Dockerfile.Trim().Length == 0 ? "Dockerfile" : Dockerfile.Trim();
        var dfAbs = Path.IsPathRooted(dfInput) ? dfInput : Path.Combine(context, dfInput);
        if (!File.Exists(dfAbs))
        {
            Fail($"Dockerfile not found: {dfAbs}");
            return;
        }

        var dockerfileRel = Path.GetRelativePath(Path.GetFullPath(context), Path.GetFullPath(dfAbs))
            .Replace('\\', '/');
        if (dockerfileRel == ".." || dockerfileRel.StartsWith("../", StringComparison.Ordinal))
        {
            Fail("The Dockerfile must be inside the build context. Adjust the build context path.");
            return;
        }

        Steps.Clear();
        Console.Clear();
        _current = null;
        _cacheHit = false;
        ProgressPercent = 0;
        StatusLine = "Starting…";
        IsDone = false;
        IsFailed = false;
        IsBuilding = true;
        _elapsed.Restart();

        var args = new Dictionary<string, string>();
        foreach (var row in BuildArgs)
        {
            if (!string.IsNullOrWhiteSpace(row.Key))
                args[row.Key.Trim()] = row.Value.Trim();
        }

        var request = new BuildRequest
        {
            ContextPath = context,
            Dockerfile = dockerfileRel,
            Tag = Tag.Trim(),
            Target = string.IsNullOrWhiteSpace(Target) ? null : Target.Trim(),
            NoCache = NoCache,
            Pull = Pull,
            BuildArgs = args,
        };

        _cts = new CancellationTokenSource();
        try
        {
            await foreach (var progress in _engine.BuildImageAsync(request, _cts.Token))
                Handle(progress);

            if (!IsFailed)
                Complete();
        }
        catch (OperationCanceledException)
        {
            Append("[build cancelled]");
            Fail("Build cancelled.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBuilding = false;
            _elapsed.Stop();
        }
    }

    private void Handle(BuildProgress progress)
    {
        if (progress.Error is { } error)
        {
            Append(error);
            _current?.MarkFailed();
            Fail(error);
            return;
        }

        var line = progress.Text;
        Append(line);

        if (ParseStep(line) is { } step)
        {
            _current?.Finish(_cacheHit);
            _cacheHit = false;

            _current = new BuildStepViewModel($"{step.Number}/{step.Total}", step.Instruction);
            Steps.Add(_current);

            ProgressPercent = step.Total > 0 ? 100.0 * step.Number / step.Total : 0;
            StatusLine = $"Step {step.Number} of {step.Total} · {step.Instruction}";
        }
        else if (line.Contains("Using cache", StringComparison.Ordinal) // classic
                 || line.Contains("CACHED", StringComparison.Ordinal))   // BuildKit
        {
            _cacheHit = true;
        }

        ElapsedText = FormatElapsed(_elapsed.Elapsed);
    }

    private void Complete()
    {
        _current?.Finish(_cacheHit);
        IsDone = true;
        ProgressPercent = 100;
        StatusLine = $"Built {Tag.Trim()}";
        ElapsedText = FormatElapsed(_elapsed.Elapsed);
        _onContextUsed?.Invoke(ContextPath.Trim());
    }

    private void Fail(string message)
    {
        IsFailed = true;
        StatusLine = message;
    }

    private void Append(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        Console.Add(line);
        while (Console.Count > MaxConsoleLines)
            Console.RemoveAt(0);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBuilding)
            _cts?.Cancel();
        else
            _onClose();
    }

    [RelayCommand]
    private void RunOnFinish() => _onRun(Tag.Trim());

    [RelayCommand]
    private void Close() => _onClose();

    /// <summary>
    /// Recognise a step-start line across builders: classic (<c>Step 3/14 : RUN …</c>),
    /// Buildah/Podman (<c>STEP 3/14: RUN …</c>) and BuildKit plain (<c>#12 [4/6] RUN …</c>).
    /// </summary>
    private static (int Number, int Total, string Instruction)? ParseStep(string line)
    {
        var s = line.Trim();

        // Classic builder: "Step 3/14 : RUN ..."
        if (s.StartsWith("Step ", StringComparison.Ordinal))
        {
            var colon = s.IndexOf(" : ", StringComparison.Ordinal);
            return colon > 0 && TryCounts(s["Step ".Length..colon], out var n, out var m)
                ? (n, m, s[(colon + 3)..].Trim())
                : null;
        }

        // Buildah (Podman): "STEP 3/14: RUN ..."
        if (s.StartsWith("STEP ", StringComparison.Ordinal))
        {
            var colon = s.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 && TryCounts(s["STEP ".Length..colon], out var n, out var m)
                ? (n, m, s[(colon + 1)..].Trim())
                : null;
        }

        // BuildKit --progress=plain: "#12 [4/6] RUN ..." or "#12 [builder 4/6] RUN ..."
        if (s.StartsWith('#'))
        {
            var open = s.IndexOf('[', StringComparison.Ordinal);
            var close = s.IndexOf(']', StringComparison.Ordinal);
            if (open > 0 && close > open)
            {
                var inside = s[(open + 1)..close].Trim();
                var space = inside.LastIndexOf(' '); // drop an optional stage name
                var counts = space >= 0 ? inside[(space + 1)..] : inside;
                if (TryCounts(counts, out var n, out var m))
                    return (n, m, s[(close + 1)..].Trim());
            }
        }

        return null;
    }

    private static bool TryCounts(string text, out int number, out int total)
    {
        number = total = 0;
        var t = text.Trim();
        var slash = t.IndexOf('/');
        return slash > 0
            && int.TryParse(t[..slash].Trim(), out number)
            && int.TryParse(t[(slash + 1)..].Trim(), out total);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{(int)elapsed.TotalSeconds}s";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A KEY=value build argument row in the Build modal.</summary>
public partial class BuildArgRow : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
}

/// <summary>One parsed build step (a Dockerfile instruction) and its live state.</summary>
public partial class BuildStepViewModel(string number, string instruction) : ObservableObject
{
    public string Number { get; } = number;
    public string Instruction { get; } = instruction;

    // running -> done | cached | failed
    [ObservableProperty] private string _state = "running";

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsCached));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(DotBrush));
        OnPropertyChanged(nameof(TagText));
        OnPropertyChanged(nameof(HasTag));
    }

    public bool IsRunning => State == "running";
    public bool IsDone => State == "done";
    public bool IsCached => State == "cached";
    public bool IsFailed => State == "failed";

    public IBrush DotBrush => new SolidColorBrush(Color.Parse(State switch
    {
        "done" => "#34D399",
        "cached" => "#5C6675",
        "running" => "#00D4A3",
        "failed" => "#F87171",
        _ => "#3A424E",
    }));

    public bool HasTag => IsCached || IsRunning;
    public string TagText => IsCached ? "CACHED" : IsRunning ? "running…" : string.Empty;

    public void Finish(bool cached) => State = cached ? "cached" : "done";
    public void MarkFailed() => State = "failed";
}
