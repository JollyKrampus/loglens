using System.Collections.Concurrent;
using System.Text;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>
/// Watches ingested lines and feeds distinct problems into the local database.
///
/// Everything here is off the hot path: fingerprinting happens on the UI thread but
/// is pure regex over a single line, and the database write is queued and flushed on
/// a timer from a background thread. A log storm therefore costs a bounded amount of
/// work per line and never blocks tailing on a disk write.
/// </summary>
public sealed class IssueRecorder : IDisposable
{
    /// <summary>Beyond this the queue drops oldest — a runaway log must not eat memory.</summary>
    private const int MaxQueued = 50_000;

    /// <summary>How many continuation lines to keep as the stack trace sample.</summary>
    private const int MaxDetailLines = 30;

    private readonly ConcurrentQueue<IssueOccurrence> _queue = new();
    private readonly System.Threading.Timer _flushTimer;
    private readonly AppSettings _settings;
    private int _flushing;

    public IssueStore Store { get; }

    /// <summary>Raised after a flush that actually wrote something.</summary>
    public event Action? Recorded;

    public IssueRecorder(IssueStore store, AppSettings settings)
    {
        Store = store;
        _settings = settings;
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
    }

    /// <summary>
    /// Scans a freshly ingested batch. Lines below Warn are ignored; a qualifying
    /// line absorbs the stack frames that follow it so the issue carries a trace.
    /// </summary>
    public void Observe(string viewName, string sourceName, IReadOnlyList<LogLine> lines)
    {
        if (!_settings.TrackIssues || lines.Count == 0) return;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Severity is not (Severity.Warn or Severity.Error or Severity.Fatal)) continue;

            // Absorb the continuation lines that belong to this event.
            var detail = new StringBuilder();
            var detailLines = new List<string>();

            for (int j = i + 1; j < lines.Count && detailLines.Count < MaxDetailLines; j++)
            {
                var next = lines[j];
                if (next.Severity != Severity.None) break;

                // Either signal marks continuation detail: the shape of the text
                // (stack frame, exception header) or the tab's timestamp-based
                // flag — which is what captures free-form spill like
                // "STDOUT: ****Fatal error received…" so the stack frames after
                // it aren't orphaned from their issue.
                if (!next.IsContinuation && !SignatureBuilder.IsContinuation(next.Text)) break;

                detailLines.Add(next.Text);
                detail.AppendLine(next.Text);
            }

            var fingerprint = SignatureBuilder.Build(line.Text, detailLines);

            _queue.Enqueue(new IssueOccurrence(
                fingerprint,
                line.Severity,
                line.Text,
                detailLines.Count > 0 ? detail.ToString().TrimEnd() : null,
                viewName,
                sourceName,
                (line.Timestamp ?? DateTime.Now).ToUniversalTime()));

            // Skip past the lines we just absorbed.
            i += detailLines.Count;
        }

        while (_queue.Count > MaxQueued) _queue.TryDequeue(out _);
    }

    /// <summary>Writes whatever has queued up. Safe to call from anywhere.</summary>
    public void Flush()
    {
        // One flush at a time; a second tick while writing simply returns.
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;

        try
        {
            if (_queue.IsEmpty) return;

            var batch = new List<IssueOccurrence>(Math.Min(_queue.Count, 5000));
            while (batch.Count < 5000 && _queue.TryDequeue(out var item)) batch.Add(item);

            if (batch.Count == 0) return;

            Store.Record(batch);
            Recorded?.Invoke();
        }
        catch
        {
            // Issue tracking is a convenience; never let it disturb tailing.
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    public void Dispose()
    {
        // Wait (bounded) for a timer flush already in flight — closing the store under
        // a write would drop that batch and could leave the WAL un-checkpointed.
        using (var drained = new ManualResetEvent(false))
        {
            if (_flushTimer.Dispose(drained)) drained.WaitOne(2000);
        }

        Flush();
        Store.Dispose();
    }
}
