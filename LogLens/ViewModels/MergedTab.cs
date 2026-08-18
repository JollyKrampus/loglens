using System.Windows.Threading;
using LogLens.Models;

namespace LogLens.ViewModels;

/// <summary>
/// One time-ordered stream across every file in a view.
///
/// The hard part is that files are polled independently, so file B's 12:00:01 line
/// can arrive before file A's 12:00:00 line. Sorting the whole buffer on every batch
/// would be O(n log n) several times a second, which is not viable at 200k lines.
///
/// Instead this uses a watermark, the same trick stream processors use: incoming
/// lines sit in a holding buffer and are only released once they are older than the
/// merge window. Every source is polled well within that window, so by release time
/// we know nothing earlier is still coming and the flush is a plain sorted append.
///
/// A source that stalls for longer than the window still breaks the assumption, so
/// that case is detected and repaired with a full re-sort — correct, and rare enough
/// that its cost does not matter.
/// </summary>
public sealed class MergedTab : LogPaneVm
{
    /// <summary>
    /// Holds the owning tab rather than just the line, so the source name, colour and
    /// index are read at release time. Baking them into the line at creation went
    /// stale whenever a tab was added or removed and the sources were re-indexed.
    /// </summary>
    private sealed record Held(LogTab Tab, LogLine Line, long ArrivalMs);

    private readonly List<Held> _held = new();
    private readonly List<LogTab> _sources = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Func<string> _describeSources;

    private DateTime? _lastReleased;
    private long _nextNumber = 1;
    private int _lateArrivals;
    private int _resorts;

    public Action? ReloadAllRequested;

    /// <summary>Set by the owning view: switches to a file tab and reveals a line there.</summary>
    public Action<LogTab, LogLine>? NavigateToSourceRequested;

    /// <summary>The file tab a merged line came from, resolved by its source index.</summary>
    public LogTab? SourceTabFor(LogLine? line)
        => line is null ? null : _sources.FirstOrDefault(t => t.SourceIndex == line.SourceIndex);

    public MergedTab(AppSettings settings, Func<string> describeSources) : base(settings)
    {
        _describeSources = describeSources;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _flushTimer.Tick += (_, __) => Release(false);
        _flushTimer.Start();
    }

    public override string Header => "Merged";
    public override string PathSpec => _describeSources();
    public override bool ShowsSourceColumn => true;

    public override bool Paused
    {
        get => base.Paused;
        set
        {
            base.Paused = value;
            if (!value) Release(true);
        }
    }

    // ---- wiring -----------------------------------------------------------------

    public void Attach(IEnumerable<LogTab> tabs)
    {
        foreach (var t in _sources) t.LinesAppended -= OnSourceLines;
        _sources.Clear();

        foreach (var t in tabs)
        {
            _sources.Add(t);
            t.LinesAppended += OnSourceLines;
        }

        UpdateStatus();
    }

    private void OnSourceLines(LinesAppendedEventArgs e)
    {
        if (e.Rewound)
        {
            // That file restarted and its own buffer has already been cleared. Rebuild
            // from what every source currently holds; dropping only the pending lines
            // would leave the pre-rotation copies behind and the file would appear
            // twice as the tailer re-delivered it.
            Reseed();
            return;
        }

        long now = Environment.TickCount64;
        foreach (var line in e.Lines)
            _held.Add(new Held(e.Tab, line, now));

        // Pausing must not let the holding buffer grow without limit.
        int cap = Math.Max(1000, Settings.MaxLines);
        if (_held.Count > cap) _held.RemoveRange(0, _held.Count - cap);
    }

    /// <summary>
    /// Rebuilds the whole merged buffer from the sources' current contents. Used when
    /// the set of sources changes, when one rotates, and when the tab is first shown —
    /// anything that would otherwise leave stale or duplicated lines behind.
    /// </summary>
    public void Reseed()
    {
        _held.Clear();
        _lastReleased = null;
        _nextNumber = 1;
        ResetBuffer();

        var gathered = new List<(LogTab Tab, LogLine Line)>();
        foreach (var tab in _sources)
            foreach (var line in tab.Buffer)
                gathered.Add((tab, line));

        gathered.Sort(static (a, b) =>
        {
            int c = Nullable.Compare(a.Line.Timestamp, b.Line.Timestamp);
            if (c != 0) return c;
            c = a.Tab.SourceIndex.CompareTo(b.Tab.SourceIndex);
            if (c != 0) return c;
            return a.Line.Number.CompareTo(b.Line.Number);
        });

        var built = new List<LogLine>(gathered.Count);
        foreach (var (tab, line) in gathered)
            built.Add(Stamp(tab, line));

        AppendLines(built, out _);

        if (built.Count > 0) _lastReleased = built[^1].Timestamp;
        UpdateStatus();
    }

    /// <summary>
    /// Re-numbers a line for the merged view and stamps its current source, keeping
    /// the original file-tab number so navigation back to the source is exact.
    /// </summary>
    private LogLine Stamp(LogTab tab, LogLine line)
        => new(_nextNumber++, line.Text, line.Rule, line.Timestamp,
               tab.Header, tab.SourceIndex, tab.SourceColor, line.SourceLineNumber);

    // ---- release ----------------------------------------------------------------

    /// <summary>
    /// Moves everything past the watermark out of the holding buffer, in timestamp
    /// order. <paramref name="all"/> forces the buffer to drain regardless of age.
    /// </summary>
    private void Release(bool all)
    {
        if (Paused || _held.Count == 0) return;

        long cutoff = Environment.TickCount64 - Settings.MergeWindowMs;

        var ready = new List<Held>();
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            if (!all && _held[i].ArrivalMs > cutoff) continue;
            ready.Add(_held[i]);
            _held.RemoveAt(i);
        }

        if (ready.Count == 0) return;

        // Sort by time, then by source, then by the line's own order within its file.
        // The last two keys make the result deterministic when timestamps tie, which
        // they routinely do at millisecond resolution.
        ready.Sort(static (a, b) =>
        {
            int c = Nullable.Compare(a.Line.Timestamp, b.Line.Timestamp);
            if (c != 0) return c;
            c = a.Tab.SourceIndex.CompareTo(b.Tab.SourceIndex);
            if (c != 0) return c;
            return a.Line.Number.CompareTo(b.Line.Number);
        });

        // Renumber so the merged view has its own continuous line numbers, and take
        // the source identity from the tab as it is right now.
        var lines = new List<LogLine>(ready.Count);
        foreach (var h in ready) lines.Add(Stamp(h.Tab, h.Line));

        var first = lines[0].Timestamp;
        bool outOfOrder = first is not null && _lastReleased is not null && first < _lastReleased;

        AppendLines(lines, out _);

        var last = lines[^1].Timestamp;
        if (last is not null && (_lastReleased is null || last > _lastReleased))
            _lastReleased = last;

        if (outOfOrder)
        {
            _lateArrivals++;
            ResortAll();
        }

        UpdateStatus();
    }

    /// <summary>
    /// Repair path for a source that fell behind the merge window. Sorts the whole
    /// buffer and rebuilds the display. Expensive, so only reached when the watermark
    /// assumption is actually violated.
    /// </summary>
    private void ResortAll()
    {
        All.Sort(static (a, b) =>
        {
            int c = Nullable.Compare(a.Timestamp, b.Timestamp);
            if (c != 0) return c;
            c = a.SourceIndex.CompareTo(b.SourceIndex);
            if (c != 0) return c;
            return a.Number.CompareTo(b.Number);
        });

        _resorts++;
        Rebuild();
    }

    private void UpdateStatus()
    {
        if (_sources.Count == 0)
        {
            Status = "No files in this view";
            return;
        }

        var undetected = _sources.Where(s => s.TimestampFormatName == "none detected").ToList();

        if (undetected.Count == _sources.Count)
        {
            Status = "No timestamps recognised — showing arrival order";
        }
        else if (undetected.Count > 0)
        {
            Status = $"No timestamp found in: {string.Join(", ", undetected.Select(u => u.Header))}";
        }
        else
        {
            Status = null;
        }

        Raise(nameof(HasError));
        Raise(nameof(MergeInfo));
    }

    /// <summary>Diagnostics for the status bar.</summary>
    public string MergeInfo
    {
        get
        {
            var parts = new List<string> { $"{_sources.Count} file(s)" };
            if (_lateArrivals > 0) parts.Add($"{_lateArrivals} late batch(es) re-sorted");
            return string.Join(" · ", parts);
        }
    }

    public override void ReloadFromDisk()
    {
        _held.Clear();
        _lastReleased = null;
        _nextNumber = 1;
        ResetBuffer();
        RaiseStats();
        ReloadAllRequested?.Invoke();
    }

    public override void ClearBuffer()
    {
        _held.Clear();
        _lastReleased = null;
        base.ClearBuffer();
    }

    public override void Dispose()
    {
        _flushTimer.Stop();
        foreach (var t in _sources) t.LinesAppended -= OnSourceLines;
        _sources.Clear();
        base.Dispose();
    }
}
