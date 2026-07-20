using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;
using Kontena.Engines;

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
    private readonly Stopwatch _elapsed = new();

    private CancellationTokenSource? _cts;
    private BuildStepViewModel? _current;
    private bool _cacheHit;

    public BuildImageViewModel(IContainerEngine engine, Action onClose, Action<string> onRun)
    {
        _engine = engine;
        _onClose = onClose;
        _onRun = onRun;
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
        if (!File.Exists(Path.Combine(context, Dockerfile.Trim())))
        {
            Fail($"Dockerfile not found: {Path.Combine(context, Dockerfile.Trim())}");
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
            Dockerfile = Dockerfile.Trim(),
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
        else if (line.Contains("Using cache", StringComparison.Ordinal))
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

    private static (int Number, int Total, string Instruction)? ParseStep(string line)
    {
        if (!line.StartsWith("Step ", StringComparison.Ordinal))
            return null;

        var colon = line.IndexOf(" : ", StringComparison.Ordinal);
        if (colon < 0)
            return null;

        var counts = line["Step ".Length..colon].Trim();
        var slash = counts.IndexOf('/');
        if (slash < 0
            || !int.TryParse(counts[..slash], out var n)
            || !int.TryParse(counts[(slash + 1)..], out var m))
            return null;

        return (n, m, line[(colon + 3)..].Trim());
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
        "running" => "#22D3AA",
        "failed" => "#F87171",
        _ => "#3A424E",
    }));

    public bool HasTag => IsCached || IsRunning;
    public string TagText => IsCached ? "CACHED" : IsRunning ? "running…" : string.Empty;

    public void Finish(bool cached) => State = cached ? "cached" : "done";
    public void MarkFailed() => State = "failed";
}
