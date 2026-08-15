using System.Windows;
using System.Windows.Media;
using LogLens.Models;

namespace LogLens.ViewModels;

/// <summary>
/// One rendered line. Kept deliberately small — there can be hundreds of thousands
/// of these alive, and the ListBox only realises the visible ones.
/// </summary>
public sealed class LogLine
{
    public LogLine(long number, string text, HighlightRule? rule,
                   DateTime? timestamp = null, string? sourceName = null,
                   int sourceIndex = 0, Brush? sourceBrush = null)
    {
        Number = number;
        Text = text;
        Rule = rule;
        Timestamp = timestamp;
        SourceName = sourceName;
        SourceIndex = sourceIndex;
        SourceBrush = sourceBrush;
    }

    public long Number { get; }
    public string Text { get; }
    public HighlightRule? Rule { get; }

    /// <summary>
    /// Parsed from the line, or inherited from the previous line when this one has
    /// none — which is what keeps a stack trace glued beneath the error that threw it
    /// in the merged timeline.
    /// </summary>
    public DateTime? Timestamp { get; }

    /// <summary>Which file this came from. Only shown in the merged view.</summary>
    public string? SourceName { get; }
    public int SourceIndex { get; }
    public Brush? SourceBrush { get; }

    public Severity Severity => Rule?.Severity ?? Severity.None;

    /// <summary>Null lets the ListBoxItem inherit the theme foreground.</summary>
    public Brush? Foreground => Rule?.ForegroundBrush;
    public Brush? Background => Rule?.BackgroundBrush;
    public FontWeight Weight => Rule is { Bold: true } ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>Copy carrying a freshly resolved rule, used when highlighting changes.</summary>
    public LogLine WithRule(HighlightRule? rule)
        => new(Number, Text, rule, Timestamp, SourceName, SourceIndex, SourceBrush);

    public override string ToString() => Text;
}
