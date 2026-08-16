using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The PromQL check-and-preview block from the rule editor mockup (KON-209): the "<c>.prev</c>"
/// panel under the expression field — a chip, a one-line summary, and the matching series.
/// <para>
/// Asks this cluster's Prometheus rather than linting locally, because <c>promtool check rules</c>
/// only confirms the syntax, and a misspelled label is always syntactically valid — its only
/// symptom is a rule that never fires. <see cref="ExprCheck.MatchesNothing"/> is the warning that
/// evaluation surfaces and a linter cannot.
/// </para>
/// <para>
/// Debounced the same way as the command-bar search (<see cref="MainWindowViewModel"/>): a burst of
/// keystrokes costs one request, not one per letter. Small and self-contained on purpose — KON-210's
/// rule editor is the intended host, but nothing here depends on it existing yet.
/// </para>
/// </summary>
public partial class PromqlCheckViewModel : ObservableObject, IDisposable
{
    private readonly IAlertSource _alerts;

    /// <summary>How long typing settles before Prometheus is asked.</summary>
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private CancellationTokenSource? _pending;

    public PromqlCheckViewModel(IAlertSource alerts) => _alerts = alerts;

    [ObservableProperty] private string _expression = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasResult), nameof(HasError), nameof(MatchesNothing), nameof(HasSamples),
        nameof(ChipText), nameof(ChipBrushKey), nameof(ChipSoftBrushKey), nameof(ChipIconKey),
        nameof(Summary), nameof(Samples))]
    private ExprCheck? _result;

    [ObservableProperty] private bool _isChecking;

    /// <summary>The pending check, so a test can await it instead of sleeping and hoping.</summary>
    internal Task Settled { get; private set; } = Task.CompletedTask;

    partial void OnExpressionChanged(string value)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        Result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            Settled = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _pending = cts;
        Settled = CheckAsync(value, cts.Token);
    }

    private async Task CheckAsync(string expression, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var check = await _alerts.CheckExprAsync(expression, ct);
            if (!ct.IsCancellationRequested)
                Result = check;
        }
        finally
        {
            IsChecking = false;
        }
    }

    public bool HasResult => Result is not null;
    public bool HasError => Result is { Parsed: false };
    public bool MatchesNothing => Result is { MatchesNothing: true };
    public bool HasSamples => Result is { Samples.Count: > 0 };

    public string ChipText => Result is { Parsed: false } ? "error" : "parses";

    /// <summary>
    /// The chip stays "parses" once Prometheus accepted the expression — <see cref="MatchesNothing"/>
    /// is not a parse failure, it is a rule that would never fire, so the colour warns without
    /// relabelling what actually happened.
    /// </summary>
    public string ChipBrushKey => Result switch
    {
        { Parsed: false } => "Danger",
        { MatchesNothing: true } => "Warn",
        _ => "Success",
    };

    /// <summary>The chip's tinted background, paired with <see cref="ChipBrushKey"/>.</summary>
    public string ChipSoftBrushKey => ChipBrushKey + "Soft";

    public string ChipIconKey => HasError ? "IconWarning" : "IconCheck";

    /// <summary>
    /// One line, same rule as the nodes-notice (KON-204): name the gap rather than hide it. A missing
    /// Prometheus surfaces here as an ordinary <see cref="ExprCheck.Error"/> — the field never claims
    /// the expression is fine when it could not be asked.
    /// </summary>
    public string Summary => Result switch
    {
        null => string.Empty,
        { Parsed: false, Error: { Length: > 0 } error } => error,
        { Parsed: false } => "Prometheus rejected the expression and did not say why.",
        { MatchesNothing: true } =>
            "Prometheus evaluated it just now · 0 series match — this rule would never fire.",
        var r => $"Prometheus evaluated it just now · {r.Samples.Count} "
            + (r.Samples.Count == 1 ? "series matches" : "series match"),
    };

    public IReadOnlyList<ExprSampleRow> Samples =>
        Result is { Samples.Count: > 0 } r ? [.. r.Samples.Select(s => new ExprSampleRow(s))] : [];

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>One matching series, formatted for the preview table.</summary>
public sealed class ExprSampleRow
{
    internal ExprSampleRow(ExprSample sample)
    {
        LabelText = "{" + string.Join(", ", sample.Labels
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .Select(l => $"{l.Key}=\"{l.Value}\"")) + "}";

        Value = sample.Value;
        ValueText = double.IsNaN(Value) ? "NaN"
            : double.IsPositiveInfinity(Value) ? "+Inf"
            : double.IsNegativeInfinity(Value) ? "-Inf"
            : Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public string LabelText { get; }
    public double Value { get; }
    public string ValueText { get; }
}
