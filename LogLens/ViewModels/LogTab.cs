using System.Windows.Media;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.ViewModels;

/// <summary>Fired when a file tab ingests new lines, so the merged view and the alert service can react.</summary>
public sealed record LinesAppendedEventArgs(LogTab Tab, IReadOnlyList<LogLine> Lines, bool Rewound);

/// <summary>One tailed file.</summary>
public sealed class LogTab : LogPaneVm
{
    private readonly List<string> _pending = new();
    private readonly TimestampExtractor _clock = new();

    private LogTailer? _tailer;
    private long _nextNumber = 1;

    public LogSource Source { get; }

    /// <summary>Position within the view — used as a stable tie-break when merging.</summary>
    public int SourceIndex { get; set; }

    /// <summary>Colour for this file in the merged view's source column.</summary>
    public Brush SourceBrush { get; set; } = Brushes.Gray;

    public event Action<LinesAppendedEventArgs>? LinesAppended;

    public LogTab(LogSource source, AppSettings settings) : base(settings)
    {
        Source = source;

        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LogSource.Name) or nameof(LogSource.Path)) Raise(nameof(Header));
            if (e.PropertyName == nameof(LogSource.Path)) Restart();
        };
        Settings.PropertyChanged += OnSettingsChanged;
    }

    public override string Header => Source.Name;
    public override string PathSpec => Source.Path;

    /// <summary>Resolved fresh each time, so a wildcard tab opens today's file, not yesterday's.</summary>
    public override string? ResolvedFilePath => PathResolver.Resolve(Source.Path);
    public override bool SupportsOpenInEditor => true;

    /// <summary>Which timestamp layout was detected, shown in the merged view's status.</summary>
    public string TimestampFormatName => _clock.FormatName;

    // ---- lifecycle --------------------------------------------------------------

    public void Start(RuleSet rules)
    {
        Rules = rules;
        Restart();
    }

    public void Restart()
    {
        _tailer?.Dispose();
        _tailer = null;

        _pending.Clear();
        _clock.Reset();
        ResetBuffer();
        _nextNumber = 1;
        RaiseStats();

        if (string.IsNullOrWhiteSpace(Source.Path))
        {
            Status = "No file selected";
            return;
        }

        // Date-less layouts need a base date; the file's own write date beats today's.
        try
        {
            var resolved = PathResolver.Resolve(Source.Path);
            if (resolved is not null) _clock.SetBaseDate(System.IO.File.GetLastWriteTime(resolved));
        }
        catch { /* today is a fine fallback */ }

        _tailer = new LogTailer(Source.Path, Settings.InitialTailKb * 1024);
        _tailer.Batch += OnBatch;
        _tailer.StatusChanged += m => Ui.BeginInvoke(() =>
        {
            Status = m;
            Raise(nameof(HasError));
        });
        _tailer.Start(Settings.PollIntervalMs);
    }

    public override void ReloadFromDisk() => _ = _tailer?.ReloadAsync();

    public override void ClearBuffer()
    {
        _pending.Clear();
        base.ClearBuffer();
    }

    private void OnSettingsChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.PollIntervalMs))
            _tailer?.SetInterval(Settings.PollIntervalMs);
        else if (e.PropertyName == nameof(AppSettings.MaxLines))
            TrimIfNeeded();
    }

    // ---- ingest -----------------------------------------------------------------

    private void OnBatch(TailBatch batch)
    {
        Ui.BeginInvoke(() =>
        {
            if (batch.Rewound)
            {
                _pending.Clear();
                _clock.Reset();
                ResetBuffer();
                _nextNumber = 1;
                LinesAppended?.Invoke(new LinesAppendedEventArgs(this, Array.Empty<LogLine>(), true));
            }

            if (Paused)
            {
                _pending.AddRange(batch.Lines);

                // Pause holds lines back, but a busy log left paused must not grow
                // the buffer without limit. Oldest go first, same as the ring buffer.
                int cap = Math.Max(1000, Settings.MaxLines);
                if (_pending.Count > cap) _pending.RemoveRange(0, _pending.Count - cap);

                RaiseStats();
                return;
            }

            Ingest(batch.Lines);
        });
    }

    protected override void FlushPending()
    {
        if (_pending.Count == 0) return;
        var copy = _pending.ToArray();
        _pending.Clear();
        Ingest(copy);
    }

    private void Ingest(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) { RaiseStats(); return; }

        var built = new List<LogLine>(texts.Count);

        foreach (var text in texts)
        {
            var rule = Rules.Match(text);

            // A line with no timestamp of its own (a stack frame, an exception header)
            // inherits the previous one, which is what keeps an exception block
            // together instead of scattering it across the merged timeline.
            var ts = _clock.Read(text) ?? _clock.Last;

            built.Add(new LogLine(_nextNumber++, text, rule, ts, Source.Name, SourceIndex, SourceBrush));
        }

        AppendLines(built, out int newAlerts);
        LinesAppended?.Invoke(new LinesAppendedEventArgs(this, built, false));

        if (newAlerts > 0) AlertsDetected?.Invoke(this, built);
    }

    /// <summary>Raised with the batch that contained errors, for the notification service.</summary>
    public event Action<LogTab, IReadOnlyList<LogLine>>? AlertsDetected;

    public override void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        _tailer?.Dispose();
        _tailer = null;
        base.Dispose();
    }
}
