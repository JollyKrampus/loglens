using System.Globalization;
using System.Text.RegularExpressions;

namespace LogLens.Services;

/// <summary>One recognised way of writing a timestamp at the start of a log line.</summary>
public sealed class TimestampFormat
{
    public required string Name { get; init; }
    public required Regex Pattern { get; init; }

    /// <summary>Exact formats to try. Empty means "let DateTime.TryParse work it out".</summary>
    public string[] Formats { get; init; } = [];

    /// <summary>True when the format carries a time but no date (Serilog's console sink).</summary>
    public bool TimeOnly { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Finds the timestamp in a log line.
///
/// The formats are ordered most-specific first and detection simply counts which
/// one matches the most sample lines, so we never have to ask the user what their
/// layout is. One regex covers ISO-8601, NLog's ${longdate}, log4net's comma
/// milliseconds, bracketed dates and JSON string values, because it searches for
/// the shape anywhere in the line rather than anchoring to the start.
/// </summary>
public static class TimestampParser
{
    public static readonly TimestampFormat[] Formats =
    [
        new()
        {
            // 2026-08-14T12:52:40.0529815-06:00 / 2026-08-14 12:52:40.0472 / ...,123
            Name = "ISO 8601 / NLog longdate",
            Pattern = new Regex(
                @"(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)
        },
        new()
        {
            // 14/08/2026 12:52:40 and 08/14/2026 12:52:40 — ambiguous, so parsed
            // with the invariant month-first convention and flagged in the name.
            Name = "Slash date (M/d/yyyy)",
            Pattern = new Regex(
                @"(?<ts>\d{1,2}/\d{1,2}/\d{4}[T ]\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:\s*[AP]M)?)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            Formats =
            [
                "M/d/yyyy H:mm:ss", "M/d/yyyy H:mm:ss.fff", "M/d/yyyy h:mm:ss tt",
                "M/d/yyyy hh:mm:ss tt", "M/d/yyyy H:mm:ss.ffff"
            ]
        },
        new()
        {
            // syslog: Aug 14 12:52:40
            Name = "Syslog (MMM d HH:mm:ss)",
            Pattern = new Regex(
                @"(?<ts>[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            Formats = ["MMM d HH:mm:ss", "MMM dd HH:mm:ss"]
        },
        new()
        {
            // Serilog console sink: [12:52:40 INF]
            Name = "Time only (HH:mm:ss)",
            Pattern = new Regex(
                @"^\W{0,2}(?<ts>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            Formats = ["HH:mm:ss", "HH:mm:ss.f", "HH:mm:ss.ff", "HH:mm:ss.fff", "HH:mm:ss.ffff"],
            TimeOnly = true
        },
    ];

    /// <summary>
    /// Picks the format that matches the most sample lines. Continuation lines such
    /// as stack frames legitimately carry no timestamp, so a format only has to
    /// explain a fifth of the sample to win.
    /// </summary>
    public static TimestampFormat? Detect(IEnumerable<string> sample)
    {
        var lines = sample.Where(l => !string.IsNullOrWhiteSpace(l)).Take(300).ToList();
        if (lines.Count == 0) return null;

        TimestampFormat? best = null;
        int bestHits = 0;

        foreach (var fmt in Formats)
        {
            int hits = 0;
            foreach (var line in lines)
                if (TryExtract(fmt, line, DateTime.Today, out _)) hits++;

            if (hits > bestHits)
            {
                bestHits = hits;
                best = fmt;
            }
        }

        return bestHits >= Math.Max(2, lines.Count / 5) ? best : null;
    }

    public static bool TryExtract(TimestampFormat fmt, string line, DateTime baseDate, out DateTime value)
    {
        value = default;

        var m = fmt.Pattern.Match(line);
        if (!m.Success) return false;

        // log4net writes milliseconds after a comma; normalise so parsing is uniform.
        var text = m.Groups["ts"].Value.Replace(',', '.');

        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal
                                    | DateTimeStyles.AssumeLocal;

        if (fmt.Formats.Length > 0)
        {
            if (!DateTime.TryParseExact(text, fmt.Formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var exact))
                return false;
            value = exact;
        }
        else
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsed))
                return false;
            value = parsed.ToLocalTime();
        }

        if (fmt.TimeOnly)
            value = baseDate.Date + value.TimeOfDay;
        else if (fmt.Formats.Length > 0 && value.Year == 1)
            value = new DateTime(baseDate.Year, value.Month, value.Day,
                                 value.Hour, value.Minute, value.Second, value.Millisecond);

        return true;
    }
}

/// <summary>
/// Per-file timestamp reader. Stateful on purpose: formats that omit the date
/// (Serilog console) or the year (syslog) need a base date, and need to roll it
/// forward when the clock wraps past midnight.
/// </summary>
public sealed class TimestampExtractor
{
    private TimestampFormat? _format;
    private TimestampFormat? _provisional;
    private bool _detected;
    private DateTime _baseDate = DateTime.Today;
    private DateTime? _last;

    private readonly List<string> _sample = new(300);

    /// <summary>
    /// The current best guess: the detected format once detection settles, the
    /// provisional one before that. After detection concludes "no timestamps
    /// here", this is null — the provisional guess is dead, and treating it as
    /// alive once turned whole keyword-only files severity-less.
    /// </summary>
    public TimestampFormat? Format => _format ?? (_detected ? null : _provisional);

    public bool IsDetected => _detected;

    /// <summary>
    /// True only when detection has run to a verdict AND found a format. This is
    /// the gate for continuation semantics: a provisional guess (set by ANY
    /// warm-up line that merely embeds a date in its message text) must never
    /// strip severity from a file the verdict later calls timestampless.
    /// </summary>
    public bool HasSettledFormat => _detected && _format is not null;

    /// <summary>
    /// Feeds a line into detection — the ONLY thing that does, so each line is
    /// sampled exactly once. The tab calls this for a whole batch before
    /// classifying it, so that on a large initial read detection settles before
    /// the first line is classified — otherwise the head of the buffer is
    /// classified under warm-up rules and never corrected. Cheap and idempotent
    /// once detection has settled.
    /// </summary>
    public void ObserveForDetection(string line)
    {
        if (_detected) return;
        _sample.Add(line);
        if (_sample.Count >= 120)
        {
            _format = TimestampParser.Detect(_sample);
            _detected = true;
            _sample.Clear();
        }
    }

    /// <summary>
    /// Once detection has run its verdict is final, including a verdict of "nothing
    /// here looks like a timestamp". Before that, report the provisional guess.
    /// </summary>
    public string FormatName =>
        _format?.Name ?? (_detected ? "none detected" : _provisional?.Name ?? "none detected");

    public void SetBaseDate(DateTime date) => _baseDate = date.Date;

    /// <summary>Reset when the underlying file is replaced or reloaded.</summary>
    public void Reset()
    {
        _format = null;
        _provisional = null;
        _detected = false;
        _last = null;
        _sample.Clear();
    }

    /// <summary>
    /// Returns the line's timestamp, or null if the line has none (a stack frame,
    /// say). Deliberately does NOT feed detection: the tab pre-observes every
    /// batch via <see cref="ObserveForDetection"/> before reading it, and sampling
    /// here as well counted head-of-batch lines twice — enough duplicate hits to
    /// flip the verdict and call a keyword-only file timestamped.
    /// </summary>
    public DateTime? Read(string line)
    {
        // Detection needs a sample, but the very first lines of a file still need
        // timestamps — otherwise they sort to the front of the merged timeline
        // regardless of when they actually happened. Until detection settles, use
        // whichever known format explains this particular line, in priority order.
        var fmt = _format;
        if (fmt is null && !_detected)
        {
            foreach (var candidate in TimestampParser.Formats)
            {
                if (!TimestampParser.TryExtract(candidate, line, _baseDate, out _)) continue;
                fmt = candidate;
                _provisional = candidate;
                break;
            }
        }

        if (fmt is null) return null;
        if (!TimestampParser.TryExtract(fmt, line, _baseDate, out var ts)) return null;

        // Date-less formats wrap at midnight; a large jump backwards means a new day.
        if (fmt.TimeOnly && _last is { } prev && ts < prev.AddHours(-12))
        {
            _baseDate = _baseDate.AddDays(1);
            ts = ts.AddDays(1);
        }

        _last = ts;
        return ts;
    }

    /// <summary>The last timestamp seen, so continuation lines can inherit it.</summary>
    public DateTime? Last => _last;
}
