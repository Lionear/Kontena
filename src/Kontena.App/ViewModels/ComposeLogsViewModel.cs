using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// Aggregated project logs: a combined, colour-per-service stream for a whole Compose
/// project. Fans in each service's <see cref="IContainerEngine.StreamLogsAsync"/> — no
/// Compose CLI needed — tagging every line with its service and a stable colour.
/// </summary>
public partial class ComposeLogsViewModel : ViewModelBase, IDisposable
{
    private const int MaxLines = 4000;

    // A readable, theme-neutral palette cycled per service.
    private static readonly string[] Palette =
    [
        "#5B9CF8", "#34D399", "#F5B14C", "#C084FC",
        "#F87171", "#22D3EE", "#A3E635", "#FB7185",
    ];

    private readonly IContainerEngine _engine;
    private readonly Action _onClose;
    private readonly IReadOnlyList<ComposeLogSource> _sources;
    private readonly List<ComposeLogLine> _all = [];
    private CancellationTokenSource? _cts;

    public ComposeLogsViewModel(
        IContainerEngine engine, string project, IReadOnlyList<ComposeLogSource> sources, Action onClose)
    {
        _engine = engine;
        Project = project;
        _sources = sources;
        _onClose = onClose;

        Legend = sources
            .Select((s, i) => new ComposeServiceLegend(s.Service, BrushFor(i)))
            .ToList();

        Start();
    }

    public string Project { get; }
    public IReadOnlyList<ComposeServiceLegend> Legend { get; }
    public ObservableCollection<ComposeLogLine> Lines { get; } = [];

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _autoScroll = true;

    partial void OnFilterChanged(string value)
    {
        Lines.Clear();
        foreach (var line in _all)
            if (Matches(line))
                Lines.Add(line);
    }

    [RelayCommand]
    private void ToggleFollow() => AutoScroll = !AutoScroll;

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Lines.Clear();
    }

    [RelayCommand]
    private void Close() => _onClose();

    // ── Streaming ─────────────────────────────────────────────────────────────

    private void Start()
    {
        _cts = new CancellationTokenSource();
        for (var i = 0; i < _sources.Count; i++)
            _ = StreamServiceAsync(_sources[i], BrushFor(i), _cts.Token);
    }

    private async Task StreamServiceAsync(ComposeLogSource source, IBrush brush, CancellationToken ct)
    {
        try
        {
            // No ConfigureAwait(false): stay on the UI context so collection edits are safe.
            await foreach (var entry in _engine.StreamLogsAsync(source.ContainerId, follow: true, ct))
                Append(new ComposeLogLine(source.Service, brush, entry.Message));
        }
        catch (OperationCanceledException)
        {
            // modal closed
        }
        catch
        {
            // one service hiccuping must not take down the whole aggregated view
        }
    }

    private void Append(ComposeLogLine line)
    {
        _all.Add(line);

        ComposeLogLine? dropped = null;
        if (_all.Count > MaxLines)
        {
            dropped = _all[0];
            _all.RemoveAt(0);
        }

        if (dropped is not null && Lines.Count > 0 && ReferenceEquals(Lines[0], dropped))
            Lines.RemoveAt(0);

        if (Matches(line))
            Lines.Add(line);
    }

    private bool Matches(ComposeLogLine line) =>
        string.IsNullOrWhiteSpace(Filter)
        || line.Service.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || line.Text.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SolidColorBrush BrushFor(int index) =>
        new(Color.Parse(Palette[index % Palette.Length]));

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A service to fan in for aggregated logs: its display name and container id.</summary>
public sealed record ComposeLogSource(string Service, string ContainerId);

/// <summary>One aggregated log line, tagged with its originating service and colour.</summary>
public sealed record ComposeLogLine(string Service, IBrush ServiceBrush, string Text);

/// <summary>A legend entry mapping a service to its colour.</summary>
public sealed record ComposeServiceLegend(string Service, IBrush Brush);
