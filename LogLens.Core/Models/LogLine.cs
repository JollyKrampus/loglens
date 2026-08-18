namespace LogLens.Models;

/// <summary>
/// One rendered line. Kept deliberately small — there can be hundreds of thousands
/// of these alive, and the list virtualisation only realises the visible ones.
///
/// Colours are hex strings, not brushes: this type lives in the cross-platform core
/// and must not know which UI framework will paint it. The view layer converts hex
/// to its own brush type (and caches, so the cost stays per-rule, not per-line).
/// </summary>
public sealed class LogLine
{
    public LogLine(long number, string text, HighlightRule? rule,
                   DateTime? timestamp = null, string? sourceName = null,
                   int sourceIndex = 0, string? sourceColor = null,
                   long? sourceLineNumber = null, bool isContinuation = false)
    {
        Number = number;
        Text = text;
        Rule = rule;
        Timestamp = timestamp;
        SourceName = sourceName;
        SourceIndex = sourceIndex;
        SourceColor = sourceColor;
        SourceLineNumber = sourceLineNumber ?? number;
        IsContinuation = isContinuation;
    }

    /// <summary>
    /// True when this line had no timestamp of its own in a file whose lines carry
    /// them — spill-over detail of the event above (stack trace, captured STDOUT).
    /// Rule re-resolution must keep honouring this, or a rules/filter change would
    /// let a loose severity keyword in the spill re-promote the line.
    /// </summary>
    public bool IsContinuation { get; }

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

    /// <summary>Hex colour for the merged view's source column.</summary>
    public string? SourceColor { get; }

    /// <summary>
    /// The line's number in its ORIGINAL file tab. The merged timeline renumbers its
    /// copies, so navigation back to the source tab needs this — matching by text
    /// and timestamp instead picks the wrong occurrence whenever a log repeats
    /// identical lines within one timestamp.
    /// </summary>
    public long SourceLineNumber { get; }

    public Severity Severity => Rule?.Severity ?? Severity.None;

    /// <summary>Hex colour; null lets the row inherit the theme foreground.</summary>
    public string? Foreground => string.IsNullOrEmpty(Rule?.Foreground) ? null : Rule!.Foreground;
    public string? Background => string.IsNullOrEmpty(Rule?.Background) ? null : Rule!.Background;
    public bool Bold => Rule is { Bold: true };

    /// <summary>Copy carrying a freshly resolved rule, used when highlighting changes.</summary>
    public LogLine WithRule(HighlightRule? rule)
        => new(Number, Text, rule, Timestamp, SourceName, SourceIndex, SourceColor, SourceLineNumber, IsContinuation);

    public override string ToString() => Text;
}
