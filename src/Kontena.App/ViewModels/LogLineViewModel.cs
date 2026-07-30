using System.Globalization;
using Avalonia.Media;
using Kontena.Sdk.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>Display wrapper around a single <see cref="LogEntry"/> in the log console.</summary>
public sealed class LogLineViewModel
{
    private static readonly string[] KnownLevels =
        ["FATAL", "ERROR", "ERR", "WARN", "WARNING", "INFO", "DEBUG", "TRACE", "READY", "OK"];

    public LogLineViewModel(LogEntry entry)
    {
        Timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Raw = entry.Message;

        var (level, rest) = Split(entry.Message);
        Level = level;
        HasLevel = level.Length > 0;
        Message = rest;

        // stderr with no explicit level still reads as an error line.
        var effective = level.Length > 0 ? level
            : entry.Source == LogSource.Stderr ? "ERROR" : string.Empty;
        LevelBrush = BrushFor(effective);
        MessageBrush = entry.Source == LogSource.Stderr && level.Length == 0
            ? Danger : TextDim;
    }

    public string Timestamp { get; }
    public string Level { get; }
    public bool HasLevel { get; }
    public string Message { get; }

    /// <summary>The full untouched line; used by the text filter.</summary>
    public string Raw { get; }

    public IBrush LevelBrush { get; }
    public IBrush MessageBrush { get; }

    /// <summary>Peel a leading level token (e.g. "WARN foo") off the message, if present.</summary>
    private static (string Level, string Body) Split(string message)
    {
        var trimmed = message.TrimStart();
        var space = trimmed.IndexOf(' ');
        var first = space < 0 ? trimmed : trimmed[..space];

        foreach (var lvl in KnownLevels)
        {
            if (string.Equals(first, lvl, StringComparison.OrdinalIgnoreCase))
                return (lvl.ToUpperInvariant(), space < 0 ? string.Empty : trimmed[(space + 1)..].TrimStart());
        }

        return (string.Empty, message);
    }

    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#5AB8FF"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F5B14C"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#5C6675"));

    private static IBrush BrushFor(string level) => level switch
    {
        "FATAL" or "ERROR" or "ERR" => Danger,
        "WARN" or "WARNING" => Warn,
        "READY" or "OK" => Success,
        "INFO" => Info,
        "DEBUG" or "TRACE" => TextFaint,
        _ => TextDim,
    };
}
