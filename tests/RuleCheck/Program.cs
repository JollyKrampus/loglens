using System.IO;
using System.Text;
using LogLens.Models;
using LogLens.Services;
using LogLens.ViewModels;

namespace RuleCheck;

/// <summary>
/// Checks that each highlight preset classifies real log lines the way it claims to.
/// Deliberately not a unit-test framework — it is one file you can run with
/// `dotnet run --project tests\RuleCheck` and read the output of.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _skipped;

    private static int Main()
    {
        var generic = new RuleSet([], HighlightRule.Defaults());

        var nlogPipe = new RuleSet([], Preset("NLog / log4net (pipe-delimited)"));
        var nlogJson = new RuleSet([], Preset("NLog JsonLayout"));

        // ---- NLog stock file layout -------------------------------------------
        // ${longdate}|${level:uppercase=true}|${logger}|${message}

        Section("NLog pipe-delimited layout");

        Expect(nlogPipe, "2026-08-14 12:52:40.0472|ERROR|Acme.Payments.PaymentGateway|Timeout calling payments after 864ms",
            Severity.Error, "plain ERROR line");

        Expect(nlogPipe, "2026-08-14 12:52:40.0835|FATAL|Acme.Jobs.NightlyBatch|Unrecoverable state, shutting down",
            Severity.Fatal, "FATAL outranks ERROR");

        Expect(nlogPipe, "2026-08-14 12:52:40.0835|WARN|Acme.Infrastructure.CacheWarmer|Retry 116 of 3",
            Severity.Warn, "WARN line");

        Expect(nlogPipe, "2026-08-14 12:52:40.0835|INFO|Acme.Jobs.NightlyBatch|Request handled in 268ms",
            Severity.Info, "INFO line");

        Expect(nlogPipe, "2026-08-14 12:52:40.0835|DEBUG|Acme.Orders.OrderService|Entering handler correlationId=5747bc31",
            Severity.Debug, "DEBUG line");

        // The case that separates anchored matching from keyword matching.
        Expect(nlogPipe, "2026-08-14 12:52:40.0835|INFO|Acme.Jobs.NightlyBatch|Recovered from a transient error after 3 attempts",
            Severity.Info, "INFO whose message says 'error' stays INFO");

        Expect(generic, "2026-08-14 12:52:40.0835|INFO|Acme.Jobs.NightlyBatch|Recovered from a transient error after 3 attempts",
            Severity.Info, "the default rules also honour a pipe-delimited level field when one exists");

        // Multi-line exception spill: coloured, but must not inflate error counts.
        ExpectRule(nlogPipe, "System.Net.Http.HttpRequestException: The operation timed out.",
            "Exception type", Severity.None, "exception header");

        ExpectRule(nlogPipe, "   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)",
            "Stack frame", Severity.None, "stack frame");

        ExpectRule(nlogPipe, " ---> System.TimeoutException: A task was canceled.",
            "Inner exception", Severity.None, "inner exception marker");

        ExpectRule(nlogPipe, "   --- End of inner exception stack trace ---",
            "Inner exception", Severity.None, "end of inner exception");

        // ---- NLog JsonLayout ---------------------------------------------------

        Section("NLog JsonLayout");

        Expect(nlogJson, """{"time":"2026-08-14T12:52:40.05-06:00","level":"Error","logger":"Acme.Payments","message":"Timeout"}""",
            Severity.Error, "level Error");

        Expect(nlogJson, """{"time":"2026-08-14T12:52:40.08-06:00","level":"Fatal","logger":"Acme.Jobs","message":"Down"}""",
            Severity.Fatal, "level Fatal");

        Expect(nlogJson, """{"time":"2026-08-14T12:52:40.08-06:00","level":"Info","logger":"Acme.Jobs","message":"Recovered from a transient error"}""",
            Severity.Info, "Info whose message says 'error' stays Info");

        Expect(nlogJson, """{"time":"2026-08-14T12:52:40.08-06:00","level":"Warn","logger":"Acme.Cache","message":"Slow"}""",
            Severity.Warn, "level Warn");

        // ---- default rules: the level field must outrank message keywords ------

        Section("Default rules, level field vs message keywords");

        // The reported case verbatim: NLog logs at level Error, but the message
        // itself begins "****Fatal error received". The level field must win.
        Expect(generic, "2026-08-18 07:12:44.1230|Error|Acme.Gateway.Listener|****Fatal error received from gateway: connection reset",
            Severity.Error, "|Error| line whose message says 'Fatal error' counts as Error");

        Expect(generic, "2026-08-18 07:12:44.1230|ERROR|Acme.Gateway.Listener|****Fatal error received from gateway: connection reset",
            Severity.Error, "same with an uppercase level field");

        Expect(generic, "2026-08-18 07:12:45.0001|Fatal|Acme.Host|Host is going down",
            Severity.Fatal, "a real |Fatal| level field still counts as Fatal");

        Expect(generic, "2026-08-18 07:12:45.0001|Warn|Acme.Cache|Error rate above threshold",
            Severity.Warn, "|Warn| line mentioning 'Error' stays Warn");

        Expect(generic, "2026-08-18 07:12:45.0001|Debug|Acme.Orders|FATAL flag parsed from config",
            Severity.Debug, "|Debug| line mentioning 'FATAL' stays Debug");

        // The field tier's vocabulary must cover everything the keyword tier calls a
        // level, or those layouts fall through and message keywords win again.
        Expect(generic, "2026-08-18 07:12:44.1230|ERR|Acme.Gateway.Listener|****Fatal error received from gateway",
            Severity.Error, "a short |ERR| level field also beats message keywords");

        Expect(generic, "2026-08-18 07:12:45.0001|Information|Acme.Jobs|Recovered from a transient error",
            Severity.Info, "an |Information| level field also beats message keywords");

        // Multi-line spill: an exception block under an |Error| line must not have
        // its continuation lines promoted to Fatal by the keyword fallback.
        ExpectRule(generic, "System.AggregateException: A fatal error occurred while receiving.",
            "Exception type", Severity.None, "exception header saying 'fatal' stays continuation, not Fatal");

        ExpectRule(generic, "   at Acme.Gateway.Listener.Receive(Message m)",
            "Stack frame", Severity.None, "stack frame stays continuation");

        Expect(generic, "Fatal error occurred",
            Severity.Fatal, "a bare keyword line with no pipes still hits the keyword tier");

        // .NET flattens inner exceptions into the event line with " ---> ". That
        // must NOT demote a leveled line to None — only a line that STARTS as a
        // continuation counts as one.
        Expect(generic, "2026-08-14 12:32:38.480 ERROR OrderService Sync failed: System.AggregateException: One or more errors occurred. ---> System.IO.IOException: disk full",
            Severity.Error, "an ERROR line with an inline ---> chain keeps its severity");

        Expect(generic, "2026-08-14 12:32:38.481 INFO StateMachine transition Idle ---> Running",
            Severity.Info, "an INFO line whose message contains ---> stays Info");

        ExpectRule(generic, " ---> System.TimeoutException: A task was canceled.",
            "Inner exception", Severity.None, "a real inner-exception continuation line stays None");

        // ---- the reported multi-line NLog event, verbatim ----------------------
        // The event line carries |ERROR|; the captured STDOUT spills onto later
        // lines with no timestamp and no level field. Continuation matching must
        // keep the spill from minting a fresh FATAL.

        Section("Multi-line events: continuations cannot declare severity");

        const string reportedEventLine =
            "2026-08-18 15:30:31.9153|ERROR|CorrespondenceSystem.CompositionService.Consumers.GetComposition|CorrespondenceSystem.CompositionService.Models.CompositionExitCodeException: Exstream exit code (12) did not indicate successful run. Path to engine run: D:\\CorrespondenceSystem\\DriversUAT01\\2026\\08\\18\\FacetsEnrollment\\Print\\946e7902-7d9c-4500-91e1-738f4dc5746f\\0";
        const string reportedStdoutLine =
            "STDOUT: ******Fatal error received trying to read the package file: ExstreamPackage.pub. *******";

        Expect(generic, reportedEventLine, Severity.Error,
            "the reported |ERROR| event line classifies as Error");

        var stdoutAsContinuation = generic.Match(reportedStdoutLine, isContinuation: true);
        Report(stdoutAsContinuation is null,
            "the STDOUT spill line, as a continuation, gets NO severity",
            $"matched '{stdoutAsContinuation?.Name}'/{stdoutAsContinuation?.Severity}");

        var stdoutAsEvent = generic.Match(reportedStdoutLine, isContinuation: false);
        Report(stdoutAsEvent?.Severity == Severity.Fatal,
            "the same text in a timestampless file still keyword-matches (documented fallback)",
            $"matched '{stdoutAsEvent?.Name}'/{stdoutAsEvent?.Severity}");

        var frameAsContinuation = generic.Match(
            "   at CorrespondenceSystem.CompositionService.Internal.ProcessTimer.WatchDirectoryAndKillHungProcess(IProcess process, String watchDir, TimeSpan overallRunTimeout, Nullable`1 singleFileTimeout) in D:\\a\\crsp-compositionservice\\crsp-compositionservice\\CorrespondenceSystem.CompositionService\\Internal\\ProcessTimer.cs:line 60",
            isContinuation: true);
        Report(frameAsContinuation?.Name == "Stack frame",
            "pure-highlight rules still colour continuation lines",
            $"matched '{frameAsContinuation?.Name}'");

        // The continuation signal itself: the event line has a readable timestamp,
        // the STDOUT line does not.
        var clock = new TimestampExtractor();
        var eventTs = clock.Read(reportedEventLine);
        var stdoutTs = clock.Read(reportedStdoutLine);
        Report(eventTs is not null && stdoutTs is null,
            "the timestamp extractor separates the event line from its spill",
            $"event={eventTs?.ToString() ?? "null"} stdout={stdoutTs?.ToString() ?? "null"}");

        // ---- pipe-format detection (drives the status-bar preset hint) ---------

        Section("Pipe-format detection");

        Report(RuleSet.LooksPipeLevelled(
            [
                "2026-08-18 07:12:44.1230|Error|Acme.Gateway|****Fatal error received",
                "2026-08-18 07:12:44.2230|Info|Acme.Gateway|Reconnecting",
                "2026-08-18 07:12:44.3230|Debug|Acme.Gateway|Attempt 1",
                "   at Acme.Gateway.Listener.Receive(Message m)",
                "2026-08-18 07:12:44.4230|Warn|Acme.Gateway|Attempt 2 slow",
                "2026-08-18 07:12:44.5230|Info|Acme.Gateway|Connected",
            ]),
            "a pipe-delimited sample with a stack line is detected", "");

        Report(!RuleSet.LooksPipeLevelled(
            [
                "2026-08-14 12:32:38.480 ERROR OrderService Unhandled 500",
                "2026-08-14 12:32:38.482 INFO OrderService Heartbeat ok",
                "2026-08-14 12:32:38.484 WARN OrderService Slow request",
                "2026-08-14 12:32:38.486 INFO OrderService Heartbeat ok",
                "2026-08-14 12:32:38.488 DEBUG OrderService Poll",
            ]),
            "a space-delimited sample is not detected as pipe format", "");

        Report(new RuleSet([], HighlightRule.Defaults()).HasLooseSeverityRules,
            "the default rules count as keyword-loose (hint applies)", "");

        Report(!new RuleSet([], Preset("NLog / log4net (pipe-delimited)")).HasLooseSeverityRules,
            "the NLog preset is fully anchored (no hint)", "");

        // ---- generic preset on space-delimited logs ----------------------------

        Section("Generic keyword preset");

        Expect(generic, "2026-08-14 12:32:38.480 ERROR OrderService Unhandled 500 from upstream",
            Severity.Error, "space-delimited ERROR");

        Expect(generic, "2026-08-14 12:32:38.472 FATAL OrderService Process is shutting down",
            Severity.Fatal, "space-delimited FATAL");

        Expect(generic, "2026-08-14 12:32:38.475 INFO OrderService Heartbeat ok, uptime 40s",
            Severity.Info, "space-delimited INFO");

        // Serilog's 3-letter levels are outside what keyword matching covers, which
        // is exactly why there is a dedicated preset for them.
        Expect(generic, "[12:52:40 INF] Acme.Orders Request finished in 32ms",
            Severity.None, "Serilog 3-letter levels are NOT matched by the generic preset");

        Section("Serilog / short levels preset");

        var shortLevels = new RuleSet([], Preset("Serilog / short levels"));

        Expect(shortLevels, "[12:52:40 INF] Acme.Orders Request finished in 32ms",
            Severity.Info, "bracketed INF");

        Expect(shortLevels, "[12:52:41 ERR] Acme.Payments Timeout calling payments",
            Severity.Error, "bracketed ERR");

        Expect(shortLevels, "[12:52:41 FTL] Acme.Jobs Host terminated unexpectedly",
            Severity.Fatal, "bracketed FTL");

        Expect(shortLevels, "[12:52:41 WRN] Acme.Cache Pool at 130%",
            Severity.Warn, "bracketed WRN");

        Expect(shortLevels, "[12:52:41 INF] Acme.Jobs ERRATIC sensor reading ignored",
            Severity.Info, "'ERRATIC' does not trip the ERR rule");

        CheckTimestamps();
        CheckMergeOrdering();
        CheckAlerts();
        CheckSounds();
        CheckSeverityChips();
        CheckFind();
        CheckWorkspaceCompat();
        CheckLegacyRuleUpgrade();
        CheckContinuationSemantics();
        CheckUpdater();
        CheckTailer();
        CheckSignatures();
        CheckIssueStore();

        Console.WriteLine();
        var skipNote = _skipped > 0 ? $" ({_skipped} skipped)" : "";

        if (_failures == 0)
        {
            Console.WriteLine($"All checks passed{skipNote}.");
            return 0;
        }

        Console.WriteLine($"{_failures} check(s) FAILED{skipNote}.");
        return 1;
    }

    // ================= timestamps =================

    private static void CheckTimestamps()
    {
        Section("Timestamp detection");

        // Each case: sample lines, then the expected format name and the expected
        // parsed value of the first line.
        CheckFormat("NLog longdate",
            ["2026-08-14 12:52:40.0472|ERROR|Acme|boom", "2026-08-14 12:52:41.1000|INFO|Acme|ok"],
            "ISO 8601 / NLog longdate", "2026-08-14 12:52:40.0472");

        CheckFormat("ISO with offset (JSON @t / time field)",
            ["""{"time":"2026-08-14T12:52:40.0500000-06:00","level":"Error"}""",
             """{"time":"2026-08-14T12:52:41.0000000-06:00","level":"Info"}"""],
            "ISO 8601 / NLog longdate", null);

        CheckFormat("log4net comma milliseconds",
            ["2026-08-14 12:52:40,123 ERROR Acme boom", "2026-08-14 12:52:41,456 INFO Acme ok"],
            "ISO 8601 / NLog longdate", "2026-08-14 12:52:40.1230");

        CheckFormat("Serilog console, time only",
            ["[12:52:40 INF] Acme Request finished", "[12:52:41 WRN] Acme Slow"],
            "Time only (HH:mm:ss)", null);

        CheckFormat("syslog",
            ["Aug 14 12:52:40 host sshd[1]: accepted", "Aug 14 12:52:41 host sshd[1]: closed"],
            "Syslog (MMM d HH:mm:ss)", null);

        // A stack frame carries no timestamp — the merged view depends on this
        // returning null so the line can inherit the one above it.
        var ex = new TimestampExtractor();
        foreach (var l in new[] { "2026-08-14 12:52:40.0472|ERROR|Acme|boom" }) ex.Read(l);
        var frame = ex.Read("   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)");
        Report(frame is null, "stack frame has no timestamp of its own",
            $"expected null, got {frame}");
        Report(ex.Last?.ToString("yyyy-MM-dd HH:mm:ss.ffff") == "2026-08-14 12:52:40.0472",
            "previous timestamp is retained for continuation lines",
            $"got {ex.Last?.ToString("yyyy-MM-dd HH:mm:ss.ffff") ?? "null"}");
    }

    /// <summary>
    /// <paramref name="expectedFirst"/> is compared as "yyyy-MM-dd HH:mm:ss.ffff"
    /// rather than as a DateTime: NLog's ${longdate} carries 100-nanosecond ticks,
    /// so ".0472" is 47.2 ms and never equals a whole-millisecond DateTime.
    /// </summary>
    private static void CheckFormat(string what, string[] lines, string expectedFormat, string? expectedFirst)
    {
        var ex = new TimestampExtractor();
        var results = lines.Select(ex.Read).ToList();

        bool nameOk = ex.FormatName == expectedFormat;
        bool parsedOk = results[0] is not null;
        bool valueOk = expectedFirst is null
                       || results[0]?.ToString("yyyy-MM-dd HH:mm:ss.ffff") == expectedFirst;

        // Whatever the format, the timestamps must come out strictly increasing.
        bool orderOk = results[0] is not null && results[1] is not null && results[1] > results[0];

        Report(nameOk && parsedOk && valueOk && orderOk, what,
            $"format='{ex.FormatName}' (wanted '{expectedFormat}'), "
            + $"first={results[0]?.ToString("yyyy-MM-dd HH:mm:ss.ffff") ?? "null"}"
            + (expectedFirst is not null ? $" (wanted {expectedFirst})" : "")
            + $", increasing={orderOk}");
    }

    // ================= merge ordering =================

    /// <summary>
    /// The merged timeline's comparator, exercised directly. Interleaves three
    /// sources whose lines arrive in the wrong order and checks the result comes
    /// out in timestamp order with ties broken deterministically.
    /// </summary>
    private static void CheckMergeOrdering()
    {
        Section("Merged timeline ordering");

        var t0 = new DateTime(2026, 8, 14, 12, 0, 0);

        // Arrival order deliberately scrambled across sources.
        var arrived = new List<LogLine>
        {
            Line(1, "prod  +3s", t0.AddSeconds(3), sourceIndex: 2),
            Line(1, "dev   +0s", t0,               sourceIndex: 0),
            Line(2, "dev   +4s", t0.AddSeconds(4), sourceIndex: 0),
            Line(1, "test  +1s", t0.AddSeconds(1), sourceIndex: 1),
            Line(2, "test  +2s", t0.AddSeconds(2), sourceIndex: 1),
            // Same instant from two sources: source index must break the tie.
            Line(3, "test  +5s", t0.AddSeconds(5), sourceIndex: 1),
            Line(2, "prod  +5s", t0.AddSeconds(5), sourceIndex: 2),
            // Continuation line inheriting the previous timestamp must stay put.
            Line(3, "prod  +5s (stack frame)", t0.AddSeconds(5), sourceIndex: 2),
        };

        arrived.Sort(static (a, b) =>
        {
            int c = Nullable.Compare(a.Timestamp, b.Timestamp);
            if (c != 0) return c;
            c = a.SourceIndex.CompareTo(b.SourceIndex);
            if (c != 0) return c;
            return a.Number.CompareTo(b.Number);
        });

        string[] expected =
        [
            "dev   +0s", "test  +1s", "test  +2s", "prod  +3s", "dev   +4s",
            "test  +5s", "prod  +5s", "prod  +5s (stack frame)"
        ];

        var actual = arrived.Select(l => l.Text).ToArray();
        bool ok = actual.SequenceEqual(expected);

        Report(ok, "three sources interleave into one time-ordered stream",
            "got: " + string.Join(" | ", actual));

        bool monotonic = true;
        for (int i = 1; i < arrived.Count; i++)
            if (arrived[i].Timestamp < arrived[i - 1].Timestamp) monotonic = false;

        Report(monotonic, "result is monotonic in time", "timestamps went backwards");

        Report(actual[^2] == "prod  +5s" && actual[^1] == "prod  +5s (stack frame)",
            "a continuation line stays directly beneath the line it belongs to",
            "got: " + string.Join(" | ", actual[^2..]));

        CheckMergedTabLive();
    }

    /// <summary>
    /// Everything above sorts a list with a copy of the comparator, which proves the
    /// comparator and nothing else. This drives a real MergedTab fed by real LogTabs
    /// tailing real files, so the watermark hold, the late-batch repair and the
    /// rewind reseed run exactly as they do in the app.
    ///
    /// Time is controlled rather than raced: every view-model callback goes through a
    /// <see cref="PumpedUi"/> that only runs when this thread pumps it. "Let the
    /// window pass" is therefore a sleep with nothing pumped, after which the queued
    /// flush ticks see every held line as old at once — the outcome cannot depend on
    /// where a 200 ms timer tick happened to fall between two tailers' polls.
    /// </summary>
    private static void CheckMergedTabLive()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"loglens-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        const int window = 1000;
        var ui = new PumpedUi();
        var settings = new AppSettings { PollIntervalMs = 50, InitialTailKb = 0, MergeWindowMs = window };
        var rules = new RuleSet([], HighlightRule.Defaults());

        var pathA = Path.Combine(dir, "a.log");
        var pathB = Path.Combine(dir, "b.log");
        File.WriteAllText(pathA, "");
        File.WriteAllText(pathB, "");

        var a = new LogTab(new LogSource { Name = "a", Path = pathA }, settings, ui);
        var b = new LogTab(new LogSource { Name = "b", Path = pathB }, settings, ui);

        // Indices deliberately opposite to attach order: ties must break on
        // SourceIndex, not on whichever list position the tab happens to occupy.
        a.SourceIndex = 1;
        b.SourceIndex = 0;

        var merged = new MergedTab(settings, () => "a, b", ui);
        merged.Attach([a, b]);

        var settle = TimeSpan.FromSeconds(5);

        try
        {
            a.Start(rules);
            b.Start(rules);
            WaitFor(() => { ui.Pump(); return a.IsPrimed && b.IsPrimed; }, settle);

            // Two sources whose lines interleave in time, plus a three-way tie at
            // 12:00:05. Within one release the held lines are gathered newest-first,
            // so without the (SourceIndex, Number) keys a-tie-2 would tend to come
            // out above a-tie-1.
            File.AppendAllText(pathA, FileText(
                "2026-08-14 12:00:00.000 INFO a-0",
                "2026-08-14 12:00:02.000 INFO a-2",
                "2026-08-14 12:00:04.000 INFO a-4",
                "2026-08-14 12:00:05.000 INFO a-tie-1",
                "2026-08-14 12:00:05.000 INFO a-tie-2"));
            File.AppendAllText(pathB, FileText(
                "2026-08-14 12:00:01.000 INFO b-1",
                "2026-08-14 12:00:03.000 INFO b-3",
                "2026-08-14 12:00:05.000 INFO b-tie"));

            WaitFor(() => { ui.Pump(); return a.TotalLines == 5 && b.TotalLines == 3; }, settle);
            int heldOnArrival = merged.TotalLines;

            // Half a window later: still younger than the watermark, still held.
            Thread.Sleep(window / 2);
            ui.Pump();
            int heldAtHalfWindow = merged.TotalLines;

            Report(a.TotalLines == 5 && b.TotalLines == 3 && heldOnArrival == 0 && heldAtHalfWindow == 0,
                "lines younger than the merge window are held back, not appended",
                $"tabs={a.TotalLines}/{b.TotalLines}, merged on arrival={heldOnArrival}, at half window={heldAtHalfWindow}");

            Thread.Sleep(window / 2 + 300);
            WaitFor(() => { ui.Pump(); return merged.TotalLines == 8; }, settle);

            string[] expected = ["a-0", "b-1", "a-2", "b-3", "a-4", "b-tie", "a-tie-1", "a-tie-2"];

            var released = MergedTexts(merged.Buffer);
            Report(released.SequenceEqual(expected),
                "once past the window, both sources release as one time-ordered append",
                "got: " + string.Join(" | ", released));

            Report(released[^3..].SequenceEqual(["b-tie", "a-tie-1", "a-tie-2"]),
                "timestamp ties break on SourceIndex, then on the line's own number",
                "got: " + string.Join(" | ", released[^3..]));

            Report(MergedTexts(merged.Display).SequenceEqual(expected) && !merged.MergeInfo.Contains("late"),
                "an in-window release needs no repair and the display matches the buffer",
                $"display: {string.Join(" | ", MergedTexts(merged.Display))}; info='{merged.MergeInfo}'");

            // A batch older than what was already released: the stalled-source case
            // the watermark cannot absorb. It must be repaired by a full re-sort and
            // admitted to in the status bar.
            File.AppendAllText(pathB, FileText("2026-08-14 12:00:00.500 INFO b-late"));
            WaitFor(() => { ui.Pump(); return merged.TotalLines == 9; }, settle);

            var afterLate = MergedTexts(merged.Buffer);
            Report(afterLate.SequenceEqual(["a-0", "b-late", "b-1", "a-2", "b-3", "a-4", "b-tie", "a-tie-1", "a-tie-2"])
                   && MergedTexts(merged.Display).SequenceEqual(afterLate),
                "a late batch older than the released lines is re-sorted into place",
                "got: " + string.Join(" | ", afterLate));

            Report(merged.MergeInfo.Contains("1 late batch(es) re-sorted"),
                "the status bar reports the late batch it had to repair",
                $"info='{merged.MergeInfo}'");

            // Truncate-and-rewrite a.log: its tab clears and signals Rewound, and the
            // merged view must rebuild from what the tabs now hold. Dropping only the
            // held lines would leave a's pre-rotation copies behind.
            File.WriteAllText(pathA, FileText("2026-08-14 12:00:06.000 INFO a-rotated"));
            WaitFor(() =>
            {
                ui.Pump();
                return a.TotalLines == 1 && MergedTexts(merged.Buffer).Contains("a-rotated");
            }, settle + TimeSpan.FromMilliseconds(window));

            var afterRewind = MergedTexts(merged.Buffer);
            Report(afterRewind.SequenceEqual(["b-late", "b-1", "b-3", "b-tie", "a-rotated"]),
                "a rewound source reseeds the merged view with no stale or duplicated lines",
                "got: " + string.Join(" | ", afterRewind));

            bool numbered = true;
            for (int i = 0; i < merged.Buffer.Count; i++)
                if (merged.Buffer[i].Number != i + 1) numbered = false;
            Report(numbered && merged.Buffer.All(l => l.SourceName == (l.SourceIndex == 1 ? "a" : "b")),
                "after a reseed the merged lines are renumbered and stamped with their current source",
                string.Join(" | ", merged.Buffer.Select(l => $"{l.Number}:{l.SourceName}:{l.Text}")));
        }
        finally
        {
            merged.Dispose();
            a.Dispose();
            b.Dispose();
            try { Directory.Delete(dir, true); } catch { /* temp files */ }
        }
    }

    private static string FileText(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>The message tag at the end of each merged line, for readable comparisons.</summary>
    private static string[] MergedTexts(IEnumerable<LogLine> lines)
        => lines.Select(l => l.Text[(l.Text.LastIndexOf(' ') + 1)..]).ToArray();

    /// <summary>
    /// A stand-in UI thread that queues posts until the check pumps them. Running
    /// them inline would not do: tailers and the merge flush timer post from
    /// threadpool threads, so view-model code would run concurrently on several
    /// threads at once — something the real dispatcher never allows. Draining the
    /// queue on the checking thread keeps the single-UI-thread contract and leaves
    /// the check in charge of when each callback runs.
    /// </summary>
    private sealed class PumpedUi : LogLens.Core.IUiThread
    {
        private readonly Queue<Action> _queue = new();

        public void Post(Action action)
        {
            lock (_queue) _queue.Enqueue(action);
        }

        public void Pump()
        {
            while (true)
            {
                Action next;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    next = _queue.Dequeue();
                }
                next();
            }
        }
    }

    private static LogLine Line(long number, string text, DateTime ts, int sourceIndex)
        => new(number, text, null, ts, $"src{sourceIndex}", sourceIndex);

    // ================= alerting =================

    private static void CheckAlerts()
    {
        Section("Alert decisions");

        var errorRule = new HighlightRule { Name = "Error", Severity = Severity.Error, Pattern = "ERROR" };
        var warnRule  = new HighlightRule { Name = "Warn",  Severity = Severity.Warn,  Pattern = "WARN"  };
        var fatalRule = new HighlightRule { Name = "Fatal", Severity = Severity.Fatal, Pattern = "FATAL" };

        LogLine L(string text, HighlightRule? rule) => new(1, text, rule);

        var errorBatch = new[] { L("boom ERROR", errorRule) };
        var warnBatch  = new[] { L("meh WARN", warnRule) };
        var fatalBatch = new[] { L("dead FATAL", fatalRule) };
        var quietBatch = new[] { L("all fine INFO", null) };

        // Fresh policy per case so throttling from one doesn't leak into the next.
        // The decision lives in LogLens.Core (AlertPolicy) and is shared by the WPF
        // and macOS shells, so these checks need neither.
        AlertPolicy New(Action<AlertSettings>? tweak = null)
        {
            var s = new AlertSettings { ThrottleSeconds = 0 };
            tweak?.Invoke(s);
            return new AlertPolicy(s);
        }

        Outcome(New(), false, "Prod", true, errorBatch,
            AlertOutcome.Alerted, "an ERROR alerts");

        Outcome(New(), false, "Prod", true, fatalBatch,
            AlertOutcome.Alerted, "a FATAL alerts");

        Outcome(New(), false, "Prod", true, warnBatch,
            AlertOutcome.NothingMatched, "a WARN does not alert at the default Error threshold");

        Outcome(New(s => s.MinimumSeverity = Severity.Warn), false, "Prod", true, warnBatch,
            AlertOutcome.Alerted, "a WARN alerts once the threshold is lowered");

        Outcome(New(), false, "Prod", true, quietBatch,
            AlertOutcome.NothingMatched, "a clean batch does not alert");

        Outcome(New(s => s.Enabled = false), false, "Prod", true, errorBatch,
            AlertOutcome.Disabled, "alerts off globally suppresses everything");

        Outcome(New(), false, "Dev", false, errorBatch,
            AlertOutcome.ViewMuted, "a muted view stays quiet");

        Outcome(New(), true, "Prod", true, errorBatch,
            AlertOutcome.AppInForeground, "no alert while you are looking at the app");

        Outcome(New(s => s.OnlyWhenUnfocused = false), true, "Prod", true, errorBatch,
            AlertOutcome.Alerted, "unless that check is turned off");

        // Custom pattern fires regardless of severity.
        Outcome(New(s => s.CustomPattern = "ORDER-9[0-9]{3}"), false, "Prod", true,
            new[] { L("INFO reconciled ORDER-9042", null) },
            AlertOutcome.Alerted, "a custom pattern alerts on an INFO line");

        Outcome(New(s => s.CustomPattern = "ORDER-9[0-9]{3}"), false, "Prod", true,
            new[] { L("INFO reconciled ORDER-1042", null) },
            AlertOutcome.NothingMatched, "a custom pattern that misses stays quiet");

        // An invalid custom regex must be inert, never fatal.
        Outcome(New(s => s.CustomPattern = "([unclosed"), false, "Prod", true, quietBatch,
            AlertOutcome.NothingMatched, "an invalid custom pattern is inert, not a crash");

        // Throttling: the second batch inside the window is suppressed.
        var throttled = New(s => s.ThrottleSeconds = 60);
        var first = throttled.Decide(false, "Prod", true, errorBatch, out _, out _);
        var second = throttled.Decide(false, "Prod", true, errorBatch, out _, out _);
        Report(first == AlertOutcome.Alerted && second == AlertOutcome.Throttled,
            "a log storm produces one alert, not thousands",
            $"first={first}, second={second}");

        // ...but a different view has its own budget.
        var other = throttled.Decide(false, "Test", true, errorBatch, out _, out _);
        Report(other == AlertOutcome.Alerted,
            "throttling is per view, so prod does not silence test",
            $"got {other}");

        // Counting drives the "3 new errors" wording in the notification.
        var counter = New();
        counter.Decide(false, "Prod", true,
            new[] { L("a ERROR", errorRule), L("b INFO", null), L("c ERROR", errorRule) },
            out var trigger, out int n);
        Report(n == 2 && trigger?.Text == "a ERROR",
            "the alert counts every match and reports the first",
            $"count={n}, trigger='{trigger?.Text}'");

        // Both shells word the notification through AlertPolicy, so a Windows balloon
        // and a macOS notification for the same batch say the same thing.
        var one = AlertPolicy.Title("Prod", L("x ERROR", errorRule), 1);
        var many = AlertPolicy.Title("Prod", L("x FATAL", fatalRule), 3);
        Report(one == "Prod — error" && many == "Prod — 3 new fatals",
            "the alert title names the view, the level and the count",
            $"one='{one}', many='{many}'");

        var longLine = L(new string('x', 500) + " ERROR", errorRule);
        var body = AlertPolicy.Body(longLine);
        Report(body.Length == 221 && body.EndsWith('…'),
            "a huge line is capped in the notification body",
            $"length={body.Length}");

        // The macOS sounds are their own fields, so a Mac teammate's choice never
        // overwrites the Windows one in a shared workspace.
        var macSettings = new AlertSettings();
        Report(macSettings.MacSoundFor(Severity.Error) == "Glass"
               && macSettings.MacSoundFor(Severity.Fatal) == "Basso"
               && macSettings.SoundFor(Severity.Error) == "Windows Notify.wav",
            "macOS sounds default separately from the Windows ones",
            $"mac={macSettings.MacSoundFor(Severity.Error)}/{macSettings.MacSoundFor(Severity.Fatal)}");

        macSettings.UseDistinctFatalSound = false;
        Report(macSettings.MacSoundFor(Severity.Fatal) == "Glass",
            "turning off the distinct FATAL sound applies on macOS too",
            $"got {macSettings.MacSoundFor(Severity.Fatal)}");
    }

    private static void Outcome(AlertPolicy policy, bool inForeground, string view, bool viewEnabled,
                                IReadOnlyList<LogLine> lines,
                                AlertOutcome expected, string what)
    {
        var actual = policy.Decide(inForeground, view, viewEnabled, lines, out _, out _);
        Report(actual == expected, what, $"expected {expected}, got {actual}");
    }

    // ================= find in tab =================

    /// <summary>
    /// LineFinder is the find bar's logic, shared by the WPF and macOS shells.
    /// Stepping starts from the selection, not the last hit, and wraps both ways.
    /// </summary>
    private static void CheckFind()
    {
        Section("Find in tab");

        var lines = new[]
        {
            new LogLine(1, "INFO starting", null),
            new LogLine(2, "ERROR Timeout calling payments", null),
            new LogLine(3, "INFO payments healthy", null),
            new LogLine(4, "WARN Payments slow", null),
            new LogLine(5, "INFO done", null),
        };

        var f = new LineFinder();

        f.Search(lines, "payments", isRegex: false, matchCase: false);
        Report(f.Hits.SequenceEqual(new[] { 1, 2, 3 }) && f.Status == "3 matches",
            "plain find is case-insensitive by default",
            $"hits=[{string.Join(",", f.Hits)}] status='{f.Status}'");

        f.Search(lines, "Payments", isRegex: false, matchCase: true);
        Report(f.Hits.SequenceEqual(new[] { 3 }),
            "match case narrows to the exact casing",
            $"hits=[{string.Join(",", f.Hits)}]");

        f.Search(lines, "^(ERROR|WARN)\\b", isRegex: true, matchCase: false);
        Report(f.Hits.SequenceEqual(new[] { 1, 3 }),
            "regex find matches per line",
            $"hits=[{string.Join(",", f.Hits)}]");

        f.Search(lines, "([unclosed", isRegex: true, matchCase: false);
        Report(f.Hits.Count == 0 && f.Status.Length > 0 && f.Status != "no matches",
            "an invalid find regex reports why instead of throwing",
            $"status='{f.Status}'");

        f.Search(lines, "nowhere", isRegex: false, matchCase: false);
        Report(f.Hits.Count == 0 && f.Status == "no matches",
            "a miss says so", $"status='{f.Status}'");

        var g = new LineFinder();
        int a = g.Step(lines, "payments", false, false, currentIndex: -1, direction: +1);
        int b = g.Step(lines, "payments", false, false, currentIndex: a, direction: +1);
        Report(a == 1 && b == 2 && g.Status == "2 of 3",
            "next steps forward from the selection",
            $"a={a} b={b} status='{g.Status}'");

        int wrapDown = g.Step(lines, "payments", false, false, currentIndex: 3, direction: +1);
        int wrapUp = g.Step(lines, "payments", false, false, currentIndex: 1, direction: -1);
        Report(wrapDown == 1 && wrapUp == 3,
            "next wraps to the top and previous wraps to the bottom",
            $"down={wrapDown} up={wrapUp}");

        // Stepping from a selection between hits goes to the neighbouring hit, not
        // the next one after wherever the last jump landed.
        int fromMiddle = g.Step(lines, "payments", false, false, currentIndex: 4, direction: -1);
        Report(fromMiddle == 3, "previous steps back from the selection",
            $"got {fromMiddle}");

        // New lines arriving change the count, which must force a re-scan.
        var grown = lines.Append(new LogLine(6, "ERROR payments down", null)).ToArray();
        int afterGrowth = g.Step(grown, "payments", false, false, currentIndex: 3, direction: +1);
        Report(afterGrowth == 5 && g.Status == "4 of 4",
            "new lines are found without retyping the term",
            $"got {afterGrowth} status='{g.Status}'");

        Report(g.Step(lines, "", false, false, 0, +1) == -1,
            "an empty term finds nothing", "expected -1");
    }

    // ================= severity chips =================

    private static void CheckSeverityChips()
    {
        Section("Severity chip filter");

        var all = SeverityClasses.All;
        bool carry = true;

        bool a = SeverityFilter.Passes(Severity.Debug, all, ref carry);
        bool b = SeverityFilter.Passes(Severity.None, all, ref carry);
        bool c = SeverityFilter.Passes(Severity.Fatal, all, ref carry);
        Report(a && b && c, "with every chip on, everything is shown", $"debug={a} none={b} fatal={c}");

        // The case the user asked for: error + warn + fatal at the same time.
        var ewf = SeverityClasses.Error | SeverityClasses.Warn | SeverityClasses.Fatal;
        carry = true;

        bool info = SeverityFilter.Passes(Severity.Info, ewf, ref carry);
        bool stackAfterInfo = SeverityFilter.Passes(Severity.None, ewf, ref carry);
        bool err = SeverityFilter.Passes(Severity.Error, ewf, ref carry);
        bool stackAfterError = SeverityFilter.Passes(Severity.None, ewf, ref carry);
        bool frame2 = SeverityFilter.Passes(Severity.None, ewf, ref carry);
        bool warn = SeverityFilter.Passes(Severity.Warn, ewf, ref carry);
        bool fatal = SeverityFilter.Passes(Severity.Fatal, ewf, ref carry);
        bool debug = SeverityFilter.Passes(Severity.Debug, ewf, ref carry);

        Report(!info && err && warn && fatal && !debug,
            "Error + Warn + Fatal can be shown together with Info and Debug hidden",
            $"info={info} err={err} warn={warn} fatal={fatal} debug={debug}");

        Report(stackAfterError && frame2,
            "a filtered-in error keeps its whole stack trace",
            $"frame1={stackAfterError} frame2={frame2}");

        Report(!stackAfterInfo,
            "a filtered-out info line takes its continuation with it",
            $"got {stackAfterInfo}");

        Report(SeverityFilter.ClassOf(Severity.Trace) == SeverityClasses.Debug,
            "Trace shares the Debug chip", $"got {SeverityFilter.ClassOf(Severity.Trace)}");

        carry = true;
        bool leading = SeverityFilter.Passes(Severity.None, ewf, ref carry);
        Report(leading, "unclassified lines before any classified line stay visible", $"got {leading}");

        // The merged-view case the review reproduced: sources interleave, so a single
        // carry attached file A's stack trace to file B's INFO. The per-source map
        // must follow each file's own thread of continuation.
        var map = new SeverityCarryMap();
        var errOnly = SeverityClasses.Error;

        bool aErr = map.Passes(0, Severity.Error, errOnly);   // file A: ERROR
        bool bInfo = map.Passes(1, Severity.Info, errOnly);   // file B: INFO, interleaved
        bool aFrame = map.Passes(0, Severity.None, errOnly);  // file A's stack frame, after B
        bool bCont = map.Passes(1, Severity.None, errOnly);   // file B's continuation

        Report(aErr && !bInfo && aFrame && !bCont,
            "in a merged buffer a stack trace follows its own file, not the interleaved neighbour",
            $"aErr={aErr} bInfo={bInfo} aFrame={aFrame} bCont={bCont}");

        map.Reset();
        Report(map.Passes(2, Severity.None, errOnly),
            "after a reset, leading unclassified lines are visible again", "");
    }

    // ================= alert sounds =================

    private static void CheckSounds()
    {
        Section("Alert sounds");

        var all = SoundLibrary.All;

        Report(all.Count >= 5, "the library always offers at least the system sounds",
            $"got {all.Count}");

        Report(all.Take(5).All(s => s.Id.StartsWith("system:")),
            "system sounds come first, since they always exist",
            "got: " + string.Join(", ", all.Take(5).Select(s => s.Id)));

        // Every non-system entry must point at a file that is actually present,
        // otherwise the dropdown offers sounds that silently fall back to a beep.
        var broken = all
            .Where(s => !s.Id.StartsWith("system:"))
            .Where(s => SoundLibrary.ResolvePath(s.Id) is null)
            .ToList();

        Report(broken.Count == 0, "every listed sound resolves to a real file",
            "unresolvable: " + string.Join(", ", broken.Select(s => s.Id)));

        Report(all.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count,
            "no duplicate entries in the dropdown",
            $"{all.Count} entries, {all.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct");

        // The shipped defaults have to exist or a fresh install is silent. Windows
        // Server images ship almost no .wav files, so on a bare CI runner this is
        // skipped rather than failed — the app already falls back to a beep there.
        bool hasMedia = all.Any(s => !s.Id.StartsWith("system:"));

        if (!hasMedia)
        {
            Skip("default alert sounds are present",
                $"no .wav files under {SoundLibrary.MediaFolder} (expected on a Windows Server image)");
        }
        else
        {
            Report(SoundLibrary.ResolvePath(SoundLibrary.DefaultSound) is not null,
                $"the default alert sound '{SoundLibrary.DefaultSound}' is present",
                "not found under " + SoundLibrary.MediaFolder);

            Report(SoundLibrary.ResolvePath(SoundLibrary.DefaultFatalSound) is not null,
                $"the default FATAL sound '{SoundLibrary.DefaultFatalSound}' is present",
                "not found under " + SoundLibrary.MediaFolder);
        }

        // Bad input must degrade to a beep, never throw, since this runs during an alert.
        try
        {
            SoundLibrary.Play("does-not-exist.wav");
            SoundLibrary.Play(null);
            SoundLibrary.Play("");
            SoundLibrary.Play(@"Z:\nope\missing.wav");
            Report(true, "a missing or empty sound id degrades to a beep instead of throwing", "");
        }
        catch (Exception ex)
        {
            Report(false, "a missing or empty sound id degrades to a beep instead of throwing", ex.Message);
        }

        // Severity routing.
        var s1 = new AlertSettings
        {
            SoundName = "a.wav",
            FatalSoundName = "b.wav",
            UseDistinctFatalSound = true
        };
        Report(s1.SoundFor(Severity.Error) == "a.wav" && s1.SoundFor(Severity.Fatal) == "b.wav",
            "FATAL uses its own sound when that option is on",
            $"error={s1.SoundFor(Severity.Error)}, fatal={s1.SoundFor(Severity.Fatal)}");

        s1.UseDistinctFatalSound = false;
        Report(s1.SoundFor(Severity.Fatal) == "a.wav",
            "FATAL falls back to the main sound when the option is off",
            $"fatal={s1.SoundFor(Severity.Fatal)}");
    }

    // ================= issue grouping =================

    private static void CheckSignatures()
    {
        Section("Issue signatures");

        // The whole feature rests on this: the same fault logged repeatedly, with
        // different ids, timings and counts, must collapse to one hash.
        var a = SignatureBuilder.Build(
            "2026-08-14 12:52:40.0472|ERROR|Acme.Payments.PaymentGateway|Timeout calling payments after 864ms for order 5512");
        var b = SignatureBuilder.Build(
            "2026-08-15 03:11:09.9911|ERROR|Acme.Payments.PaymentGateway|Timeout calling payments after 12ms for order 99013");

        Report(a.Hash == b.Hash, "the same fault with different numbers and timestamps groups together",
            $"a={a.Hash} b={b.Hash}\n        sigA={a.Signature}\n        sigB={b.Signature}");

        // ...but genuinely different faults must not be merged.
        var c = SignatureBuilder.Build(
            "2026-08-14 12:52:40.0472|ERROR|Acme.Payments.PaymentGateway|Card declined for order 5512");
        Report(a.Hash != c.Hash, "different messages stay separate", $"both hashed to {a.Hash}");

        // Same message text from a different component is a different problem.
        var d = SignatureBuilder.Build(
            "2026-08-14 12:52:40.0472|ERROR|Acme.Orders.OrderService|Timeout calling payments after 864ms for order 5512");
        Report(a.Hash != d.Hash, "the same message from a different logger stays separate",
            $"both hashed to {a.Hash}");

        // GUIDs, paths, URLs and quoted values are all per-occurrence noise.
        var e1 = SignatureBuilder.Build(@"ERROR Worker Failed job 3f2504e0-4f89-11d3-9a0c-0305e82c3301 at C:\jobs\a.json from https://api.x/y ""batch-1""");
        var e2 = SignatureBuilder.Build(@"ERROR Worker Failed job 8ab3d0f1-1111-2222-3333-444455556666 at D:\other\b.json from https://api.z/w ""batch-99""");
        Report(e1.Hash == e2.Hash, "guids, paths, urls and quoted values are masked",
            $"\n        {e1.Signature}\n        {e2.Signature}");

        // Each remaining mask, pinned to its exact output. The hash is the database
        // key, so any change here splits every stored issue whose message contains
        // that kind of token into a new row — orphaning its count and Jira key. These
        // also pin the ORDER of the masks: a timestamp must not be eaten as a time
        // plus numbers, an IP not as four numbers, a GUID not as hex fragments.
        (string In, string Out, string What)[] masks =
        [
            ("Job for 2026-08-14T12:52:40.123+02:00 did not run", "Job for <ts> did not run", "an ISO timestamp inside the message"),
            ("Heartbeat missed at 12:52:40.123", "Heartbeat missed at <time>", "a time of day"),
            ("Connect to 10.0.0.12:5432 refused", "Connect to <ip> refused", "an IP with port"),
            (@"Cannot read \\fileserver\share\in\x.csv", "Cannot read <path>", "a UNC path"),
            ("Mail to ops.team+alerts@acme-corp.example.com bounced", "Mail to <email> bounced", "an email address"),
            ("Access violation at 0x7FFE12AB", "Access violation at <hex>", "a 0x hex literal"),
            ("Bad token 9f86d081884c7d659a2feaa0c55ad015", "Bad token <hex>", "a long bare hex string"),
            ("Lease 3f2504e0-4f89-11d3-9a0c-0305e82c3301 expired", "Lease <guid> expired", "a GUID, whole"),
            ("Key 'customer-42' missing", "Key '<s>' missing", "a single-quoted value"),
            ("Took 864ms using 12.5MB", "Took <n> using <n>", "numbers with units"),
            ("Queue at 97% after 1,024 retries", "Queue at <n>% after <n> retries", "percentages and grouped numbers"),
            ("Disk   full\ton  node", "Disk full on node", "runs of whitespace"),
            ("Entering handler correlationId=5747bc31", "Entering handler correlationId=<hex>", "a short hex id"),
            ("Pool on node7 and web03 drained", "Pool on node<n> and web<n> drained", "digits inside lowercase host names"),
            ("Job tmp9xz3q stalled on worker_12", "Job tmp<n>xz<n>q stalled on worker_<n>", "digits inside generated lowercase ids"),
            ("Int32 overflow in SHA256 over HTTP2", "Int32 overflow in SHA256 over HTTP2", "uppercase type and protocol names keep their digits"),
            ("Row was added, then faced a decade of decay", "Row was added, then faced a decade of decay", "English words spelled from hex letters stay words"),
        ];

        foreach (var (input, output, what) in masks)
        {
            var masked = SignatureBuilder.Mask(input);
            Report(masked == output, $"masking: {what}", $"'{input}' -> '{masked}', expected '{output}'");
        }

        // And all of them together, varying every token, still group as one fault.
        var m1 = SignatureBuilder.Build(
            "ERROR Sync Host 10.0.0.12:5432 rejected ops@acme.example.com at 12:52:40 code 0x80004005 "
            + "trace 9f86d081884c7d659a2feaa0c55ad015 key 'k1' after 864ms");
        var m2 = SignatureBuilder.Build(
            "ERROR Sync Host 192.168.1.7:80 rejected dev+x@other.example.org at 03:11:09 code 0x8007000E "
            + "trace 0123456789abcdef0123456789abcdef key 'other' after 3s");
        Report(m1.Hash == m2.Hash, "ips, emails, times, hex and units all vary without splitting the group",
            $"\n        {m1.Signature}\n        {m2.Signature}");

        // The field report: the same fault on two hosts with two correlation ids
        // used to be two issues because neither token was masked.
        var h1 = SignatureBuilder.Build("ERROR Sync Lease lost on node7 correlationId=5747bc31");
        var h2 = SignatureBuilder.Build("ERROR Sync Lease lost on node12 correlationId=09ae44f0");
        Report(h1.Hash == h2.Hash, "host names and short correlation ids vary without splitting the group",
            $"\n        {h1.Signature}\n        {h2.Signature}");

        // An exception + stack gives a title a human can triage from.
        var withStack = SignatureBuilder.Build(
            "2026-08-14 12:52:44.1692|ERROR|Acme.Orders.OrderService|Timeout calling payments",
            [
                "System.Net.Http.HttpRequestException: The operation timed out.",
                " ---> System.TimeoutException: A task was canceled.",
                "   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)",
                "   at Acme.Orders.OrderService.PlaceAsync(Order o)"
            ]);

        Report(withStack.ExceptionType == "System.Net.Http.HttpRequestException",
            "the exception type is extracted from the stack", $"got {withStack.ExceptionType ?? "null"}");

        Report(withStack.FaultingMethod == "Acme.Payments.PaymentClient.ChargeAsync",
            "the first stack frame is taken as the faulting method",
            $"got {withStack.FaultingMethod ?? "null"}");

        Report(withStack.Title == "HttpRequestException in PaymentClient.ChargeAsync — Timeout calling payments",
            "the title reads like a bug report", $"got '{withStack.Title}'");

        // Two faults sharing an exception and faulting method must still be tellable
        // apart in the list, so the message has to reach the title.
        var sameStackOtherMessage = SignatureBuilder.Build(
            "2026-08-14 12:52:44.1692|ERROR|Acme.Orders.OrderService|Upstream returned 500 on attempt 4",
            [
                "System.Net.Http.HttpRequestException: The operation timed out.",
                "   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)"
            ]);

        Report(sameStackOtherMessage.Title != withStack.Title
               && sameStackOtherMessage.Hash != withStack.Hash,
            "two faults with the same exception and method get distinguishable titles",
            $"a='{withStack.Title}'\n        b='{sameStackOtherMessage.Title}'");

        // The same exception reached by a different call path is a different bug.
        var otherPath = SignatureBuilder.Build(
            "2026-08-14 12:52:44.1692|ERROR|Acme.Orders.OrderService|Timeout calling payments",
            [
                "System.Net.Http.HttpRequestException: The operation timed out.",
                "   at Acme.Shipping.LabelClient.CreateAsync(LabelRequest r)"
            ]);
        Report(withStack.Hash != otherPath.Hash,
            "the same exception from a different method is a separate issue",
            $"both hashed to {withStack.Hash}");

        Report(SignatureBuilder.IsContinuation("   at Acme.X.Y(Z z)")
               && SignatureBuilder.IsContinuation(" ---> System.TimeoutException: nope")
               && !SignatureBuilder.IsContinuation("2026-08-14 12:52:40.0472|INFO|Acme|fine"),
            "continuation lines are recognised, ordinary lines are not", "");

        // Serilog-style lines should still produce something sensible.
        var serilog = SignatureBuilder.Build("[12:52:41 ERR] Acme.Payments Timeout after 900ms");
        Report(serilog.Title.Length > 0 && !serilog.Title.Contains("12:52:41"),
            "a Serilog line drops its timestamp from the title", $"got '{serilog.Title}'");
    }

    private static void CheckIssueStore()
    {
        Section("Issue database");

        var path = Path.Combine(Path.GetTempPath(), $"loglens-issues-test-{Guid.NewGuid():N}.db");

        try
        {
            using (var store = new IssueStore(path))
            {
                var fp = SignatureBuilder.Build("ERROR Worker Timeout after 100ms");
                var other = SignatureBuilder.Build("FATAL Worker Host is going down");

                var now = DateTime.UtcNow;
                store.Record([
                    new IssueOccurrence(fp, Severity.Error, "ERROR Worker Timeout after 100ms", null, "Prod", "app.log", now),
                    new IssueOccurrence(fp, Severity.Error, "ERROR Worker Timeout after 250ms", null, "Prod", "app.log", now.AddSeconds(1)),
                    new IssueOccurrence(fp, Severity.Error, "ERROR Worker Timeout after 900ms", null, "Test", "other.log", now.AddSeconds(2)),
                    new IssueOccurrence(other, Severity.Fatal, "FATAL Worker Host is going down", null, "Prod", "app.log", now.AddSeconds(3)),
                ]);

                // The same fault in Prod and Test must be TWO rows — a bug fixed in
                // dev can still be live in prod, so views never share counters.
                var errors = store.Query(Severity.Error);
                var prod = errors.FirstOrDefault(i => i.View == "Prod");
                var test = errors.FirstOrDefault(i => i.View == "Test");
                Report(errors.Count == 2 && prod?.Count == 2 && test?.Count == 1,
                    "the same fault in two views stays two rows with independent counts",
                    $"rows={errors.Count}, prod={prod?.Count}, test={test?.Count}");

                Report(prod?.Sources == "app.log" && test?.Sources == "other.log",
                    "each row records its own view's source files",
                    $"prod='{prod?.Sources}' test='{test?.Sources}'");

                Report(store.Query(Severity.Error, view: "Prod").Count == 1
                       && store.Query(Severity.Error, view: "Test").Count == 1
                       && store.Query(Severity.Error, view: "Dev").Count == 0,
                    "querying by view isolates that view's issues", "");

                var views = store.DistinctViews();
                Report(views.Contains("Prod") && views.Contains("Test") && views.Count == 2,
                    "the view list for the filter dropdown is discovered from the data",
                    $"got [{string.Join(", ", views)}]");

                var counts = store.CountsBySeverity();
                Report(counts.GetValueOrDefault(Severity.Error) == 2 && counts.GetValueOrDefault(Severity.Fatal) == 1,
                    "severity counts see per-view rows",
                    $"error={counts.GetValueOrDefault(Severity.Error)}, fatal={counts.GetValueOrDefault(Severity.Fatal)}");

                Report(store.CountsBySeverity(view: "Test").GetValueOrDefault(Severity.Error) == 1
                       && store.CountsBySeverity(view: "Test").GetValueOrDefault(Severity.Fatal) == 0,
                    "severity counts can be scoped to one view", "");

                // A Jira key on the Prod row must not mark the Test row as filed.
                store.SetJiraKey(prod!.Hash, "Prod", "PLAT-1234");
                var unfiled = store.Query(Severity.Error, includeFiled: false);
                Report(unfiled.Count == 1 && unfiled[0].View == "Test",
                    "filing an issue in one view leaves the same fault open in another",
                    $"unfiled rows={unfiled.Count}");

                store.SetIgnored(test!.Hash, "Test", true);
                Report(store.Query(Severity.Error, view: "Test").Count == 0
                       && store.Query(Severity.Error, view: "Test", includeIgnored: true).Count == 1
                       && store.Query(Severity.Error, view: "Prod", includeFiled: true).Count == 1,
                    "ignoring is per view too", "ignore leaked across views");

                // A rule fix can reclassify a signature (1.5.0 called |Error| lines
                // Fatal). The stored row must follow the newest observation, or the
                // Issues window shows the wrong severity forever. Uses its own row in
                // its own view so it cannot disturb the fixtures above.
                var reclass = SignatureBuilder.Build("FATAL Worker Cache corrupted at offset 4096");
                store.Record([
                    new IssueOccurrence(reclass, Severity.Fatal, "FATAL Worker Cache corrupted at offset 4096", null, "Dev", "dev.log", now.AddSeconds(5)),
                ]);
                store.Record([
                    new IssueOccurrence(reclass, Severity.Error, "FATAL Worker Cache corrupted at offset 8192", null, "Dev", "dev.log", now.AddSeconds(6)),
                ]);
                var reclassified = store.Query(view: "Dev")
                    .FirstOrDefault(i => i.Hash == reclass.Hash);
                Report(reclassified?.Severity == Severity.Error && reclassified?.Count == 2,
                    "a re-observed issue adopts the newest severity classification",
                    $"severity={reclassified?.Severity} count={reclassified?.Count}");

                // Application (source file) filter and per-application counts, in
                // their own view so they cannot disturb the fixtures above. One
                // fault seen in two apps is ONE row listing both, so it must count
                // once for each app — and "orders-api.log" must not match the
                // look-alike "old-orders-api.log".
                var shared = SignatureBuilder.Build("ERROR Db Deadlock on table orders");
                var lookalike = SignatureBuilder.Build("ERROR Db Connection refused");
                var warnOnly = SignatureBuilder.Build("WARN Http Slow response 900ms");
                store.Record([
                    new IssueOccurrence(shared, Severity.Error, "ERROR Db Deadlock on table orders", null, "Stage", "orders-api.log", now),
                    new IssueOccurrence(shared, Severity.Error, "ERROR Db Deadlock on table orders", null, "Stage", "billing.log", now.AddSeconds(1)),
                    new IssueOccurrence(lookalike, Severity.Error, "ERROR Db Connection refused", null, "Stage", "old-orders-api.log", now.AddSeconds(2)),
                    new IssueOccurrence(warnOnly, Severity.Warn, "WARN Http Slow response 900ms", null, "Stage", "orders-api.log", now.AddSeconds(3)),
                ]);

                var ordersRows = store.Query(view: "Stage", source: "orders-api.log");
                Report(ordersRows.Count == 2 && ordersRows.All(i => i.SourceList.Contains("orders-api.log")),
                    "filtering by application matches whole source names, not substrings",
                    $"rows={ordersRows.Count} [{string.Join("; ", ordersRows.Select(i => i.Sources))}]");

                var sharedRow = ordersRows.FirstOrDefault(i => i.Hash == shared.Hash);
                Report(sharedRow is not null && sharedRow.SourceList.SequenceEqual(["orders-api.log", "billing.log"]),
                    "an issue seen in two applications lists both",
                    $"sources='{sharedRow?.Sources}'");

                var tallies = store.CountsBySource(view: "Stage");
                int TallyOf(string src) => tallies.FirstOrDefault(t => t.Source == src)?.Issues ?? -1;
                Report(tallies.Count == 3 && tallies[0].Source == "orders-api.log"
                       && TallyOf("orders-api.log") == 2 && TallyOf("billing.log") == 1
                       && TallyOf("old-orders-api.log") == 1,
                    "per-application counts are distinct issues, busiest application first",
                    $"got [{string.Join(", ", tallies.Select(t => $"{t.Source}={t.Issues}"))}]");

                var billingSev = store.CountsBySeverity(view: "Stage", source: "billing.log");
                Report(billingSev.GetValueOrDefault(Severity.Error) == 1 && billingSev.GetValueOrDefault(Severity.Warn) == 0,
                    "severity counts can be scoped to one application",
                    $"error={billingSev.GetValueOrDefault(Severity.Error)}, warn={billingSev.GetValueOrDefault(Severity.Warn)}");

                store.SetIgnored(warnOnly.Hash, "Stage", true);
                Report(store.CountsBySource(view: "Stage").First(t => t.Source == "orders-api.log").Issues == 1
                       && store.CountsBySource(includeIgnored: true, view: "Stage").First(t => t.Source == "orders-api.log").Issues == 2,
                    "per-application counts honour the ignore flag like every other count", "");

                // The generated ticket must contain the facts that make it useful.
                var fatal = store.Query(Severity.Fatal)[0];
                var ticket = JiraTemplate.Full(fatal, "PLAT");
                Report(ticket.Contains("[FATAL]") && ticket.Contains("Occurrences")
                       && ticket.Contains("Prod") && ticket.Contains(fatal.Hash),
                    "the generated ticket carries severity, counts, environment and signature",
                    "ticket was missing expected content");
            }

            // Reopening must find the data — the point is history across restarts.
            using (var reopened = new IssueStore(path))
            {
                Report(reopened.Query(Severity.Fatal).Count == 1,
                    "the database survives being closed and reopened",
                    $"found {reopened.Query(Severity.Fatal).Count} fatal issue(s)");
            }
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                    File.Delete(f);
            }
            catch { /* temp files */ }
        }

        CheckIssueStoreMigration();
    }

    /// <summary>
    /// Teams already have databases written by 1.3, which keyed on hash alone.
    /// Opening one must carry every row over — with its Jira key, notes and ignore
    /// flag — and new sightings must land in proper per-view rows beside them.
    /// </summary>
    /// <summary>A fault whose 1.3 row is keyed by its real hash, so new sightings can find it.</summary>
    private static readonly IssueFingerprint LegacyFingerprint =
        SignatureBuilder.Build("ERROR Worker Timeout after 100ms");

    private static void CheckIssueStoreMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loglens-migrate-test-{Guid.NewGuid():N}.db");

        try
        {
            // Build a database exactly as IssueStore v1 (≤1.3) created it — WAL mode
            // and BOTH v1 indexes included. The indexes matter: their names collide
            // with the v2 DDL's CREATE INDEX IF NOT EXISTS, and an index-less fixture
            // masked exactly that bug once already.
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE issues (
                        hash TEXT PRIMARY KEY, severity INTEGER NOT NULL, title TEXT NOT NULL,
                        signature TEXT NOT NULL, exception_type TEXT, faulting_method TEXT, logger TEXT,
                        sample_line TEXT NOT NULL, sample_detail TEXT, count INTEGER NOT NULL DEFAULT 0,
                        first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL,
                        views TEXT NOT NULL DEFAULT '', sources TEXT NOT NULL DEFAULT '',
                        jira_key TEXT, notes TEXT, ignored INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS ix_issues_severity ON issues(severity, last_seen_utc DESC);
                    CREATE INDEX IF NOT EXISTS ix_issues_count    ON issues(count DESC);
                    INSERT INTO issues VALUES
                        ('aaaa', 5, 'Old error', 'sig-a', NULL, NULL, NULL, 'sample', NULL, 42,
                         '2026-08-01T00:00:00.0000000Z', '2026-08-10T00:00:00.0000000Z',
                         'Prod', 'app.log', 'PLAT-99', 'my notes', 0),
                        ('bbbb', 6, 'Old fatal seen everywhere', 'sig-b', NULL, NULL, NULL, 'sample2', NULL, 7,
                         '2026-08-02T00:00:00.0000000Z', '2026-08-11T00:00:00.0000000Z',
                         'Prod,Test', 'app.log', NULL, NULL, 1);
                    """;
                cmd.ExecuteNonQuery();

                // One more 1.3 row, keyed by a REAL fingerprint hash and seen in a
                // single view — the common case. A sighting of it after the upgrade
                // must continue this row, not start a fresh one beside it.
                using var legacy = c.CreateCommand();
                legacy.CommandText = """
                    INSERT INTO issues VALUES
                        ($hash, 5, 'Legacy timeout', $sig, NULL, NULL, NULL, 'sample3', NULL, 5,
                         '2026-08-01T00:00:00.0000000Z', '2026-08-09T00:00:00.0000000Z',
                         'Prod', 'app.log', 'PLAT-7', NULL, 0);
                    """;
                legacy.Parameters.AddWithValue("$hash", LegacyFingerprint.Hash);
                legacy.Parameters.AddWithValue("$sig", LegacyFingerprint.Signature);
                legacy.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using (var store = new IssueStore(path))
            {
                var all = store.Query(includeIgnored: true, includeFiled: true, limit: 10);
                var a = all.FirstOrDefault(i => i.Hash == "aaaa");
                var b = all.FirstOrDefault(i => i.Hash == "bbbb");

                Report(a is not null && a.View == "Prod" && a.Count == 42
                       && a.JiraKey == "PLAT-99" && a.Notes == "my notes",
                    "a 1.3 row migrates with its count, Jira key and notes intact",
                    a is null ? "row missing" : $"view={a.View} count={a.Count} jira={a.JiraKey}");

                Report(b is not null && b.View == "Prod,Test" && b.Ignored,
                    "a genuinely mixed 1.3 row keeps its combined label and ignore flag",
                    b is null ? "row missing" : $"view='{b.View}' ignored={b.Ignored}");

                // New sightings after migration scope per view, next to the legacy row.
                var fp = SignatureBuilder.Build("ERROR Worker fresh problem after migration");
                store.Record([
                    new IssueOccurrence(fp, Severity.Error, "ERROR Worker fresh problem after migration",
                        null, "Dev", "dev.log", DateTime.UtcNow)
                ]);

                Report(store.Query(Severity.Error, view: "Dev").Count == 1,
                    "new sightings after migration land in per-view rows", "");

                store.Record([
                    new IssueOccurrence(LegacyFingerprint, Severity.Error, "ERROR Worker Timeout after 31ms",
                        null, "Prod", "app.log", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc))
                ]);
                var continued = store.Query(view: "Prod", includeFiled: true)
                    .Where(i => i.Hash == LegacyFingerprint.Hash).ToList();
                Report(continued.Count == 1 && continued[0].Count == 6 && continued[0].JiraKey == "PLAT-7"
                       && continued[0].FirstSeenUtc == new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
                       && continued[0].LastSeenUtc == new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
                    "a migrated single-view row is continued by new sightings, keeping its history and Jira key",
                    string.Join("; ", continued.Select(i =>
                        $"count={i.Count} jira={i.JiraKey} first={i.FirstSeenUtc:O} last={i.LastSeenUtc:O}")));
            }

            // Every launch after the upgrade opens an already-migrated database; that
            // path must leave the rows exactly as they are.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var again = new IssueStore(path))
            {
                var rows = again.Query(includeIgnored: true, includeFiled: true, limit: 10);
                var legacyRow = rows.FirstOrDefault(i => i.Hash == LegacyFingerprint.Hash);
                Report(rows.Count == 4 && legacyRow?.Count == 6
                       && rows.FirstOrDefault(i => i.Hash == "aaaa")?.Count == 42,
                    "reopening a migrated database changes nothing",
                    $"rows={rows.Count}, legacy count={legacyRow?.Count}");
            }

            // The migrated schema must match what a fresh install creates — the
            // index-name collision with v1 silently dropped ix_issues_count once.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                check.Open();
                using var cmd = check.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' " +
                                  "AND name IN ('ix_issues_view_sev','ix_issues_count')";
                var indexCount = Convert.ToInt32(cmd.ExecuteScalar());
                Report(indexCount == 2,
                    "the migrated database has both v2 indexes, same as a fresh install",
                    $"found {indexCount} of 2");
            }
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                    File.Delete(f);
            }
            catch { /* temp files */ }
        }
    }

    // ================= workspace compatibility =================

    /// <summary>
    /// Teams share workspace files across app versions, so the on-disk format is a
    /// contract. This loads a workspace exactly as v1.1.0 wrote one and asserts
    /// nothing is lost — if a schema change ever breaks older files, this fails CI
    /// before it reaches anyone. The unknown extra property pins the reverse
    /// direction: files written by a NEWER LogLens must still load in this one.
    /// </summary>
    private static void CheckWorkspaceCompat()
    {
        Section("Workspace compatibility");

        const string v11Workspace = """
        {
          "Version": 1,
          "Settings": {
            "PollIntervalMs": 300,
            "MaxLines": 150000,
            "InitialTailKb": 1024,
            "FontFamily": "Consolas",
            "FontSize": 13.0,
            "ShowLineNumbers": true,
            "WordWrap": false,
            "LightTheme": false,
            "MergeWindowMs": 1500,
            "TrackIssues": true,
            "JiraBaseUrl": "https://example.atlassian.net",
            "JiraProjectKey": "PLAT"
          },
          "Alerts": {
            "Enabled": true,
            "MinimumSeverity": "Error",
            "CustomPattern": "ORDER-9",
            "ShowToast": true,
            "PlaySound": true,
            "SoundName": "Windows Notify.wav",
            "FatalSoundName": "Windows Critical Stop.wav",
            "UseDistinctFatalSound": true,
            "FlashTaskbar": true,
            "OnlyWhenUnfocused": true,
            "ThrottleSeconds": 20
          },
          "Rules": [
            { "Name": "Error", "Pattern": "\\b(ERROR)\\b", "IsRegex": true, "CaseSensitive": false,
              "Enabled": true, "Bold": false, "Foreground": "#FF8A8A", "Background": "#3A1414", "Severity": "Error" }
          ],
          "Views": [
            {
              "Id": "team-view", "Name": "Prod", "Accent": "#EF5350",
              "ShowMergedTimeline": true, "AlertsEnabled": true,
              "Sources": [ { "Id": "s1", "Name": "app", "Path": "C:\\logs\\app-*.log" } ],
              "Rules": []
            }
          ],
          "ActiveViewId": "team-view",
          "WindowWidth": 1280, "WindowHeight": 760, "WindowMaximized": false,
          "SomeSettingFromAFutureVersion": { "nested": true, "count": 3 }
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"loglens-compat-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, v11Workspace);
            var ws = WorkspaceStore.Load(path);

            Report(ws.Settings.PollIntervalMs == 300 && ws.Settings.MaxLines == 150_000
                   && ws.Settings.JiraProjectKey == "PLAT" && ws.Settings.MergeWindowMs == 1500,
                "a v1.1 workspace's settings survive loading",
                $"poll={ws.Settings.PollIntervalMs} max={ws.Settings.MaxLines} jira={ws.Settings.JiraProjectKey}");

            Report(ws.Alerts.CustomPattern == "ORDER-9" && ws.Alerts.SoundName == "Windows Notify.wav"
                   && ws.Alerts.ThrottleSeconds == 20,
                "alert settings survive, sound choice included",
                $"pattern={ws.Alerts.CustomPattern} sound={ws.Alerts.SoundName}");

            Report(ws.Alerts.MacSoundName == "Glass" && ws.Alerts.MacFatalSoundName == "Basso",
                "a workspace from before macOS alerts gets the default Mac sounds",
                $"mac={ws.Alerts.MacSoundName}/{ws.Alerts.MacFatalSoundName}");

            Report(ws.Views.Count == 1 && ws.Views[0].Name == "Prod"
                   && ws.Views[0].ShowMergedTimeline && ws.Views[0].Sources.Count == 1
                   && ws.Views[0].Sources[0].Path == @"C:\logs\app-*.log"
                   && ws.ActiveViewId == "team-view",
                "views, sources and the active view survive",
                $"views={ws.Views.Count} active={ws.ActiveViewId}");

            Report(ws.Rules.Count == 1 && ws.Rules[0].Severity == Severity.Error,
                "highlight rules survive", $"rules={ws.Rules.Count}");

            Report(true, "an unknown property from a future version is ignored, not fatal",
                "load would have thrown before reaching here");

            // A workspace from before pane-state persistence must default to
            // everything-visible, not crash or hide lines.
            var pane = ws.Views[0].Sources[0].Pane;
            Report(pane is not null && pane.ShowFatal && pane.ShowInfo && pane.ShowDebug
                   && pane.FollowTail && pane.Include == "",
                "a pre-1.5 workspace gets default pane state (all severities shown)",
                pane is null ? "Pane was null" : $"fatal={pane.ShowFatal} include='{pane.Include}'");

            // Chips and filters must round-trip through the file.
            pane!.ShowInfo = false;
            pane.ShowDebug = false;
            pane.Include = "payment";
            pane.FilterIsRegex = true;
            ws.Views[0].MergedPane.ShowWarn = false;
            ws.Settings.AutoSaveWorkspace = false;
            ws.Alerts.MacSoundName = "Ping";

            // Round-trip: what this version saves must itself reload.
            WorkspaceStore.Save(ws, path);
            var again = WorkspaceStore.Load(path);
            Report(again.Views.Count == 1 && again.Settings.PollIntervalMs == 300,
                "saving and reloading with the current version loses nothing",
                $"views={again.Views.Count} poll={again.Settings.PollIntervalMs}");

            var paneAgain = again.Views[0].Sources[0].Pane;
            Report(!paneAgain.ShowInfo && !paneAgain.ShowDebug && paneAgain.ShowError
                   && paneAgain.Include == "payment" && paneAgain.FilterIsRegex
                   && !again.Views[0].MergedPane.ShowWarn
                   && !again.Settings.AutoSaveWorkspace
                   && again.Alerts.MacSoundName == "Ping"
                   && again.Alerts.SoundName == "Windows Notify.wav",
                "severity chips, filters, auto-save and both platforms' sounds round-trip through the workspace file",
                $"info={paneAgain.ShowInfo} include='{paneAgain.Include}' mergedWarn={again.Views[0].MergedPane.ShowWarn}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }

        CheckWorkspaceLocation();
    }

    /// <summary>
    /// Portable-first, except where "beside the exe" is a trap: a .app bundle, or a
    /// package manager's versioned folder (Scoop puts each version in a new folder,
    /// so a workspace written beside 1.5.3 is gone after an update to 1.5.4).
    /// </summary>
    private static void CheckWorkspaceLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loglens-location-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var roaming = Path.Combine(root, "roaming");
        var bundle = Path.Combine(root, "LogLens.app", "Contents", "MacOS");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(bundle);

        try
        {
            var portable = Path.Combine(app, WorkspaceStore.FileName);
            var roamed = Path.Combine(roaming, WorkspaceStore.FileName);

            Report(WorkspaceStore.ResolveDefaultPath(app, roaming) == portable,
                "a writable exe folder is the portable workspace location", WorkspaceStore.ResolveDefaultPath(app, roaming));

            Report(WorkspaceStore.ResolveDefaultPath(bundle, roaming) == roamed,
                "inside a .app bundle the workspace goes to the per-user folder", WorkspaceStore.ResolveDefaultPath(bundle, roaming));

            File.WriteAllText(Path.Combine(app, WorkspaceStore.NotPortableMarker), "");
            Report(WorkspaceStore.ResolveDefaultPath(app, roaming) == roamed,
                "a package-managed install (marker beside the exe) uses the per-user folder",
                WorkspaceStore.ResolveDefaultPath(app, roaming));

            // Even a workspace already sitting beside a managed exe is ignored: under
            // Scoop that folder is about to be replaced by the next version's.
            File.WriteAllText(portable, "{}");
            Report(WorkspaceStore.ResolveDefaultPath(app, roaming) == roamed,
                "the marker outranks a workspace file beside the exe", WorkspaceStore.ResolveDefaultPath(app, roaming));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ================= legacy default-rule upgrade =================

    /// <summary>The default rules exactly as releases 1.0–1.5.0 wrote them.</summary>
    private static List<HighlightRule> Pre151DefaultRules() =>
    [
        new() { Name = "Fatal",   Pattern = @"\b(FATAL|CRITICAL|PANIC)\b",        Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
        new() { Name = "Error",   Pattern = @"\b(ERROR|ERR|SEVERE|EXCEPTION)\b",  Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
        new() { Name = "Warning", Pattern = @"\b(WARN|WARNING)\b",                Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
        new() { Name = "Info",    Pattern = @"\b(INFO|INFORMATION)\b",            Severity = Severity.Info,  Foreground = "#8FD3FF" },
        new() { Name = "Debug",   Pattern = @"\b(DEBUG|DBG)\b",                   Severity = Severity.Debug, Foreground = "#9E9E9E" },
        new() { Name = "Trace",   Pattern = @"\b(TRACE|VERBOSE)\b",               Severity = Severity.Trace, Foreground = "#6E6E6E" },
        new() { Name = "Stack frame", Pattern = @"^\s+at\s",                      Severity = Severity.None,  Foreground = "#C58A8A" },
    ];

    /// <summary>The default rules exactly as 1.5.1 wrote them: two tiers, narrower
    /// field vocabulary, no continuation rules above the keyword tier.</summary>
    private static List<HighlightRule> V151DefaultRules()
    {
        static string Field(string levels) => $@"(^|\|)\s*({levels})\s*\|";
        var rules = new List<HighlightRule>
        {
            new() { Name = "Fatal (level field)",   Pattern = Field("FATAL|CRITICAL"), Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
            new() { Name = "Error (level field)",   Pattern = Field("ERROR|SEVERE"),   Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
            new() { Name = "Warning (level field)", Pattern = Field("WARN|WARNING"),   Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
            new() { Name = "Info (level field)",    Pattern = Field("INFO"),           Severity = Severity.Info,  Foreground = "#8FD3FF" },
            new() { Name = "Debug (level field)",   Pattern = Field("DEBUG|DBG"),      Severity = Severity.Debug, Foreground = "#9E9E9E" },
            new() { Name = "Trace (level field)",   Pattern = Field("TRACE|VERBOSE"),  Severity = Severity.Trace, Foreground = "#6E6E6E" },
        };
        rules.AddRange(Pre151DefaultRules());
        return rules;
    }

    private static void CheckLegacyRuleUpgrade()
    {
        Section("Legacy default-rule upgrade");

        var path = Path.Combine(Path.GetTempPath(), $"loglens-rules-{Guid.NewGuid():N}.json");
        try
        {
            // A workspace still carrying the untouched pre-1.5.1 defaults, written by
            // the real serialiser, must come back with the two-tier defaults.
            var old = Workspace.CreateDefault();
            old.Version = 1;                    // as every pre-1.5.1 release wrote it
            old.Rules = Pre151DefaultRules();
            WorkspaceStore.Save(old, path);

            var upgraded = WorkspaceStore.Load(path);
            var fresh = HighlightRule.Defaults();

            Report(upgraded.Rules.Count == fresh.Count
                   && upgraded.Rules[0].Name == fresh[0].Name
                   && upgraded.Rules[0].Pattern == fresh[0].Pattern
                   && upgraded.Version == Workspace.CurrentVersion,
                "untouched pre-1.5.1 defaults are upgraded to the two-tier defaults",
                $"count={upgraded.Rules.Count} first={upgraded.Rules[0].Name} version={upgraded.Version}");

            var set = new RuleSet([], upgraded.Rules);
            var match = set.Match("2026-08-18 07:12:44.1230|Error|Acme.Gateway|****Fatal error received from gateway");
            Report(match?.Severity == Severity.Error,
                "after the upgrade, |Error| beats a message mentioning 'Fatal'",
                $"got {match?.Severity.ToString() ?? "none"} via '{match?.Name ?? "none"}'");

            // One recoloured rule = the user owns the list; nothing changes.
            var custom = Workspace.CreateDefault();
            custom.Version = 1;
            custom.Rules = Pre151DefaultRules();
            custom.Rules[1].Foreground = "#FF0000";
            WorkspaceStore.Save(custom, path);

            var kept = WorkspaceStore.Load(path);
            Report(kept.Rules.Count == 7 && kept.Rules[1].Foreground == "#FF0000",
                "a customised rule set is left exactly alone",
                $"count={kept.Rules.Count} fg={kept.Rules[1].Foreground}");

            // Same for a disabled rule — that is a deliberate choice, not staleness.
            var muted = Workspace.CreateDefault();
            muted.Version = 1;
            muted.Rules = Pre151DefaultRules();
            muted.Rules[4].Enabled = false;
            WorkspaceStore.Save(muted, path);

            var keptMuted = WorkspaceStore.Load(path);
            Report(keptMuted.Rules.Count == 7 && !keptMuted.Rules[4].Enabled,
                "a rule set with a disabled rule is left alone too",
                $"count={keptMuted.Rules.Count}");

            // A workspace last saved by 1.5.1 (Version=2, untouched 13-rule set)
            // must ALSO be upgraded — its rules lack the continuation tier, so
            // stack-trace headers saying "fatal" still mint bogus Fatals there.
            var v151 = Workspace.CreateDefault();
            v151.Version = 2;
            v151.Rules = V151DefaultRules();
            WorkspaceStore.Save(v151, path);

            var upgraded151 = WorkspaceStore.Load(path);
            Report(upgraded151.Rules.Count == fresh.Count
                   && upgraded151.Rules.Any(r => r.Name == "Inner exception")
                   && upgraded151.Version == Workspace.CurrentVersion,
                "untouched 1.5.1 defaults are upgraded too",
                $"count={upgraded151.Rules.Count} version={upgraded151.Version}");

            // But a CUSTOMISED 1.5.1 set stays the user's own.
            var v151Custom = Workspace.CreateDefault();
            v151Custom.Version = 2;
            v151Custom.Rules = V151DefaultRules();
            v151Custom.Rules[0].Background = "#600000";
            WorkspaceStore.Save(v151Custom, path);

            var kept151 = WorkspaceStore.Load(path);
            Report(kept151.Rules.Count == 13 && kept151.Rules[0].Background == "#600000",
                "a customised 1.5.1 rule set is left alone",
                $"count={kept151.Rules.Count} bg={kept151.Rules[0].Background}");

            // The trap the version gate exists for: a 1.5.1 user deletes the six
            // "(level field)" rules, leaving a list content-identical to the legacy
            // defaults. Their workspace is already CurrentVersion, so the upgrade
            // must NOT run and the deletion must stick across loads.
            var trimmed = Workspace.CreateDefault();
            trimmed.Rules = trimmed.Rules.Where(r => !r.Name.Contains("(level field)")).ToList();
            int trimmedCount = trimmed.Rules.Count;
            WorkspaceStore.Save(trimmed, path);

            var stillTrimmed = WorkspaceStore.Load(path);
            Report(stillTrimmed.Rules.Count == trimmedCount
                   && stillTrimmed.Rules.All(r => !r.Name.Contains("(level field)")),
                "deleting the level-field rules on 1.5.1 sticks — no resurrection",
                $"count={stillTrimmed.Rules.Count} expected={trimmedCount}");

            // The current defaults must round-trip untouched (no repeat upgrades).
            var current = Workspace.CreateDefault();
            WorkspaceStore.Save(current, path);
            var reloaded = WorkspaceStore.Load(path);
            Report(reloaded.Rules.Count == fresh.Count,
                "current defaults round-trip without being rewritten",
                $"count={reloaded.Rules.Count}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ================= continuation semantics =================

    private static void CheckContinuationSemantics()
    {
        Section("Continuation semantics");

        // The verdict gates continuation matching: a keyword-only file where some
        // warm-up lines merely EMBED a date in message text must not lose its
        // keyword severities to a stale provisional format guess.
        var clock = new TimestampExtractor();
        for (int i = 0; i < 130; i++)
        {
            var text = i % 25 == 0
                ? $"INFO Scheduler Next nightly run at 2026-08-19 02:00:00 (slot {i})"
                : $"ERROR OrderService Unhandled 500 from upstream, attempt {i}";
            clock.ObserveForDetection(text);
        }
        Report(clock.IsDetected && !clock.HasSettledFormat && clock.Format is null,
            "embedded dates below the detection threshold leave the verdict timestampless",
            $"detected={clock.IsDetected} settled={clock.HasSettledFormat} fmt={clock.FormatName}");

        var nlogClock = new TimestampExtractor();
        for (int i = 0; i < 130; i++)
            nlogClock.ObserveForDetection($"2026-08-18 15:30:{i % 60:00}.0000|INFO|Acme.Jobs|tick {i}");
        Report(nlogClock.HasSettledFormat,
            "a genuinely pipe-timestamped file settles to a format",
            $"fmt={nlogClock.FormatName}");

        // The EXACT call pattern LogTab.Ingest uses: observe a batch, then read
        // it. Each line must be sampled once — when Read also sampled, the head
        // of the batch was counted twice and 15 embedded dates out of 130 lines
        // were enough duplicate hits to flip the verdict to "timestamped",
        // severity-stripping the whole keyword-only file.
        var ingestClock = new TimestampExtractor();
        var batch1 = new List<string>();
        for (int i = 0; i < 100; i++)
            batch1.Add(i < 15
                ? $"INFO Scheduler Job {i} scheduled for 2026-08-19 02:00:00"
                : $"ERROR OrderService Unhandled 500 from upstream, attempt {i}");
        var batch2 = new List<string>();
        for (int i = 0; i < 30; i++)
            batch2.Add($"WARN CacheWarmer Retry {i} of 3");

        foreach (var batch in new[] { batch1, batch2 })
        {
            foreach (var l in batch) ingestClock.ObserveForDetection(l);
            foreach (var l in batch) ingestClock.Read(l);
        }
        Report(ingestClock.IsDetected && !ingestClock.HasSettledFormat,
            "the ingest call pattern samples each line once — embedded dates cannot flip the verdict",
            $"detected={ingestClock.IsDetected} settled={ingestClock.HasSettledFormat} fmt={ingestClock.FormatName}");

        // The recorder absorbs timestamp-flagged spill, so free-form STDOUT and
        // the stack frames after it stay attached to their parent issue.
        var dbPath = Path.Combine(Path.GetTempPath(), $"loglens-absorb-{Guid.NewGuid():N}.db");
        var recorder = new IssueRecorder(new IssueStore(dbPath),
            new AppSettings { TrackIssues = true });
        try
        {
            var error = new HighlightRule { Name = "Error", Pattern = "unused", Severity = Severity.Error };
            var lines = new List<LogLine>
            {
                new(1, "2026-08-18 15:30:31.9153|ERROR|Acme.Gateway|Acme.Models.ExitCodeException: exit code (12) did not indicate success",
                    error, DateTime.Now),
                new(2, "STDOUT: ******Fatal error received trying to read the package file: ExstreamPackage.pub. *******",
                    null, DateTime.Now, isContinuation: true),
                new(3, "   at Acme.Gateway.ProcessTimer.Watch(IProcess p)",
                    null, DateTime.Now, isContinuation: true),
            };
            recorder.Observe("Prod", "app.log", lines);
            recorder.Flush();

            var rows = recorder.Store.Query(view: "Prod");
            var issue = rows.FirstOrDefault();
            Report(rows.Count == 1 && issue?.Severity == Severity.Error
                   && issue.SampleDetail?.Contains("STDOUT") == true
                   && issue.SampleDetail?.Contains("ProcessTimer.Watch") == true,
                "free-form spill and its stack frames absorb into the parent Error issue",
                $"rows={rows.Count} sev={issue?.Severity} detail={(issue?.SampleDetail is null ? "null" : "present")}");
        }
        finally
        {
            recorder.Dispose();
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
                    File.Delete(f);
            }
            catch { }
        }
    }

    // ================= updater =================

    private static void CheckUpdater()
    {
        Section("Self-update");

        Report(UpdateService.ParseVersion("v1.2.0") == new Version(1, 2, 0)
               && UpdateService.ParseVersion("1.10.3") == new Version(1, 10, 3)
               && UpdateService.ParseVersion("1.3.0+abc123") == new Version(1, 3, 0)
               && UpdateService.ParseVersion("v2.0.0-rc.1") == new Version(2, 0, 0),
            "release tags parse: v-prefix, build metadata and prerelease suffixes",
            $"got {UpdateService.ParseVersion("v1.2.0")}, {UpdateService.ParseVersion("1.3.0+abc123")}");

        Report(UpdateService.ParseVersion("main") is null && UpdateService.ParseVersion("") is null,
            "junk tags parse to null instead of throwing", "");

        Report(UpdateService.ParseVersion("v1.10.0") > UpdateService.ParseVersion("v1.9.9"),
            "versions compare numerically, not as strings (1.10 > 1.9)", "");

        var sums = "ed48649235605\n"
                 + "ab12cd34ef5601234567890123456789012345678901234567890123456789ab  LogLens.exe\n"
                 + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff  Other.dll\n";
        Report(UpdateService.ParseChecksum(sums, "LogLens.exe")
                   == "ab12cd34ef5601234567890123456789012345678901234567890123456789ab",
            "the right line is pulled out of SHA256SUMS.txt",
            $"got {UpdateService.ParseChecksum(sums, "LogLens.exe")}");

        Report(UpdateService.ParseChecksum(sums, "missing.exe") is null
               && UpdateService.ParseChecksum(null, "LogLens.exe") is null,
            "a missing entry or empty file yields null, not a bogus hash", "");

        // The rename dance on real files: exe -> .old, staged -> exe.
        var dir = Path.Combine(Path.GetTempPath(), $"loglens-swap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "LogLens.exe");
            var staged = exe + UpdateService.StagedSuffix;
            File.WriteAllText(exe, "OLD BINARY");
            File.WriteAllText(staged, "NEW BINARY");

            UpdateService.PerformSwap(exe, staged);

            Report(File.ReadAllText(exe) == "NEW BINARY"
                   && File.ReadAllText(exe + UpdateService.BackupSuffix) == "OLD BINARY"
                   && !File.Exists(staged),
                "the swap installs the new exe and keeps the old one as .old",
                $"exe='{File.ReadAllText(exe)}'");

            // A second update must not trip over the previous .old.
            File.WriteAllText(staged, "NEWER BINARY");
            UpdateService.PerformSwap(exe, staged);
            Report(File.ReadAllText(exe) == "NEWER BINARY",
                "a second update replaces the leftover .old without failing",
                $"exe='{File.ReadAllText(exe)}'");

            // If installing the new exe fails, the old one must come back.
            var lockedTarget = Path.Combine(dir, "Locked.exe");
            File.WriteAllText(lockedTarget, "ORIGINAL");
            bool rolledBack;
            try
            {
                UpdateService.PerformSwap(lockedTarget, Path.Combine(dir, "does-not-exist.update"));
                rolledBack = false;
            }
            catch
            {
                rolledBack = File.Exists(lockedTarget) && File.ReadAllText(lockedTarget) == "ORIGINAL";
            }
            Report(rolledBack, "a failed swap restores the original exe instead of leaving none",
                $"exists={File.Exists(lockedTarget)}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        CheckDownloadAndVerify();
        CheckLeftoverCleanup();
        CheckWaitForPredecessor();

        // Live check against the real GitHub API — opt-in because CI networking and
        // rate limits make it flaky there. Run locally: RULECHECK_LIVE=1
        if (Environment.GetEnvironmentVariable("RULECHECK_LIVE") == "1")
        {
            try
            {
                var update = UpdateService.CheckAsync(currentOverride: new Version(0, 0, 1))
                    .GetAwaiter().GetResult();

                Report(update is not null
                       && update.Latest >= new Version(1, 2, 0)
                       && update.ExeDownloadUrl.Contains("LogLens.exe")
                       && update.ChecksumDownloadUrl is not null,
                    "LIVE: the GitHub check finds the real latest release with exe and checksum",
                    update is null ? "returned null" : $"latest={update.Latest} url={update.ExeDownloadUrl}");

                var current = UpdateService.CheckAsync(currentOverride: new Version(99, 0, 0))
                    .GetAwaiter().GetResult();
                Report(current is null, "LIVE: being ahead of the latest release reports up-to-date",
                    current is null ? "" : $"unexpectedly offered {current.Latest}");
            }
            catch (Exception ex)
            {
                Skip("LIVE update check", "network problem: " + ex.Message);
            }
        }
        else
        {
            Skip("LIVE update check against api.github.com", "set RULECHECK_LIVE=1 to run it");
        }
    }

    /// <summary>
    /// DownloadAndVerifyAsync stages beside <see cref="Environment.ProcessPath"/> — when
    /// these checks run, that is this check program in its own build output, so the
    /// staged file is ours to create and delete. Run any other way (through a bare
    /// `dotnet RuleCheck.dll`, where the process is the dotnet host) it would stage
    /// beside the SDK, so that case is skipped rather than risked.
    /// </summary>
    private static string? OwnExePathOrNull()
    {
        var exe = Environment.ProcessPath;
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return exe is not null && entry is not null
               && string.Equals(Path.GetFileNameWithoutExtension(exe), entry, StringComparison.OrdinalIgnoreCase)
            ? exe
            : null;
    }

    /// <summary>
    /// The download half of the update, served from a loopback socket so nothing here
    /// touches the internet. The invariant under test: nothing unverified is ever
    /// handed to the swap, and a rejected download leaves no staged file behind.
    /// </summary>
    private static void CheckDownloadAndVerify()
    {
        var exe = OwnExePathOrNull();
        if (exe is null)
        {
            Skip("update download and checksum verification",
                $"process is '{Environment.ProcessPath}', not this check program's own exe");
            return;
        }

        var staged = exe + UpdateService.StagedSuffix;
        var payload = Encoding.ASCII.GetBytes("NEW BINARY " + Guid.NewGuid());
        var goodHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

        using var server = new LoopbackServer(new()
        {
            ["/LogLens.exe"] = payload,
            ["/good/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes($"{goodHash}  LogLens.exe\n"),
            ["/bad/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes($"{new string('f', 64)}  LogLens.exe\n"),
            ["/other/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes($"{goodHash}  Something.dll\n"),
            ["/portal/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes("<html><body>Please sign in</body></html>"),
        });

        UpdateInfo Release(string exePath, string? sumsPath) => new(
            new Version(1, 0, 0), new Version(9, 9, 9), "v9.9.9", server.BaseUrl + "/release",
            server.BaseUrl + exePath, payload.Length, sumsPath is null ? null : server.BaseUrl + sumsPath);

        // Runs one download and reports how it ended: the staged path, or the failure.
        (string? Staged, Exception? Error) Attempt(UpdateInfo info, IProgress<double>? progress = null)
        {
            try { File.Delete(staged); } catch { /* not there */ }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { return (UpdateService.DownloadAndVerifyAsync(info, progress, cts.Token).GetAwaiter().GetResult(), null); }
            catch (Exception ex) { return (null, ex); }
        }

        try
        {
            var progress = new LastProgress();
            var good = Attempt(Release("/LogLens.exe", "/good/SHA256SUMS.txt"), progress);
            Report(good.Error is null && good.Staged == staged && File.Exists(staged)
                   && File.ReadAllBytes(staged).SequenceEqual(payload) && progress.Last == 1.0,
                "a download matching its published checksum is staged beside the exe",
                $"error={good.Error?.Message ?? "none"}, staged={good.Staged ?? "null"}, progress={progress.Last}");

            var exeBefore = File.ReadAllBytes(exe);

            var bad = Attempt(Release("/LogLens.exe", "/bad/SHA256SUMS.txt"));
            Report(bad.Error is InvalidOperationException && bad.Error.Message.Contains("checksum does not match")
                   && !File.Exists(staged),
                "a checksum mismatch is refused and the staged download deleted",
                $"error={bad.Error?.GetType().Name}: {bad.Error?.Message}, stagedLeft={File.Exists(staged)}");

            // These three used to pass UNVERIFIED: verification was skipped whenever
            // there was no expected hash to compare against.
            var noSums = Attempt(Release("/LogLens.exe", null));
            var noEntry = Attempt(Release("/LogLens.exe", "/other/SHA256SUMS.txt"));
            var portal = Attempt(Release("/LogLens.exe", "/portal/SHA256SUMS.txt"));
            Report(noSums.Error is InvalidOperationException && noEntry.Error is InvalidOperationException
                   && portal.Error is InvalidOperationException && !File.Exists(staged),
                "a release with no checksum, or none for LogLens.exe, is refused rather than installed unverified",
                $"noSums={noSums.Error?.Message ?? "ACCEPTED"}; noEntry={noEntry.Error?.Message ?? "ACCEPTED"}; "
                + $"portal={portal.Error?.Message ?? "ACCEPTED"}; stagedLeft={File.Exists(staged)}");

            var missing = Attempt(Release("/missing/LogLens.exe", "/good/SHA256SUMS.txt"));
            Report(missing.Error is not null && !File.Exists(staged),
                "a failed download leaves no partial staged file behind",
                $"error={missing.Error?.Message ?? "none"}, stagedLeft={File.Exists(staged)}");

            Report(File.ReadAllBytes(exe).SequenceEqual(exeBefore),
                "download and verification never touch the running exe", "the exe changed");
        }
        finally
        {
            try { File.Delete(staged); } catch { /* not there */ }
        }
    }

    /// <summary>
    /// Startup cleanup of what earlier swaps left behind, including the .old-&lt;ticks&gt;
    /// variants PerformSwap steps aside to. It works on the running exe's folder,
    /// so — like the download — it runs only against this program's own build output.
    /// </summary>
    private static void CheckLeftoverCleanup()
    {
        var exe = OwnExePathOrNull();
        if (exe is null)
        {
            Skip("update leftover cleanup", $"process is '{Environment.ProcessPath}', not this check program's own exe");
            return;
        }

        var dir = Path.GetDirectoryName(exe)!;
        string[] leftovers =
        [
            exe + UpdateService.BackupSuffix,
            exe + UpdateService.BackupSuffix + "-638912345678901234",
            exe + UpdateService.StagedSuffix,
        ];
        // Same suffix, different exe: cleanup must stay scoped to its own name.
        var bystander = Path.Combine(dir, "SomeOtherTool.exe" + UpdateService.BackupSuffix);

        try
        {
            foreach (var f in leftovers) File.WriteAllText(f, "leftover");
            File.WriteAllText(bystander, "not ours");

            UpdateService.CleanUpLeftovers();

            var survivors = leftovers.Where(File.Exists).Select(Path.GetFileName).ToList();
            Report(survivors.Count == 0 && File.Exists(exe) && File.Exists(bystander),
                "startup cleanup removes .old, .old-<ticks> and .update, and nothing else",
                $"survivors=[{string.Join(", ", survivors)}], exe={File.Exists(exe)}, bystander={File.Exists(bystander)}");
        }
        finally
        {
            foreach (var f in leftovers) try { File.Delete(f); } catch { /* best effort */ }
            try { File.Delete(bystander); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The successor's wait for its predecessor is bounded — a hung old instance must
    /// delay startup, never prevent it — and is a no-op for a normal launch.
    /// </summary>
    private static void CheckWaitForPredecessor()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UpdateService.WaitForPredecessor([]);
        UpdateService.WaitForPredecessor(["--some-other-flag", "123"]);
        UpdateService.WaitForPredecessor([UpdateService.UpdatedFromArg, "not-a-pid"]);
        UpdateService.WaitForPredecessor([UpdateService.UpdatedFromArg, int.MaxValue.ToString()]);
        var quick = sw.ElapsedMilliseconds;

        Report(quick < 1000,
            "a normal launch, a junk pid or an already-exited predecessor does not wait",
            $"took {quick} ms");

        // Our own process never exits during the call, so this is the hung-predecessor case.
        sw.Restart();
        UpdateService.WaitForPredecessor([UpdateService.UpdatedFromArg, Environment.ProcessId.ToString()], maxWaitMs: 300);
        var bounded = sw.ElapsedMilliseconds;

        Report(bounded >= 250 && bounded < 3000,
            "waiting on a predecessor that never exits gives up at the bound",
            $"took {bounded} ms for a 300 ms bound");
    }

    /// <summary>Records progress synchronously; Progress&lt;T&gt; would post it to the thread pool.</summary>
    private sealed class LastProgress : IProgress<double>
    {
        public double Last { get; private set; }
        public void Report(double value) => Last = value;
    }

    /// <summary>
    /// A minimal HTTP/1.1 responder on a loopback port the OS picks, so the update
    /// download can be exercised without the internet and without an HttpListener
    /// URL reservation (http.sys can demand admin rights for one on Windows). Serves fixed
    /// bodies by path; anything else is a 404.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly Dictionary<string, byte[]> _routes;
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }

        public LoopbackServer(Dictionary<string, byte[]> routes)
        {
            _routes = routes;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        int n = await stream.ReadAsync(buffer, _cts.Token);
                        if (n <= 0) return;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }

                    var target = head.ToString().Split(' ', 3)[1];
                    bool found = _routes.TryGetValue(target, out var body);
                    body ??= [];

                    var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n"
                                 + $"Content-Length: {body.Length}\r\n"
                                 + "Content-Type: application/octet-stream\r\n"
                                 + "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                    await stream.WriteAsync(body, _cts.Token);
                }
            }
            catch { /* the client went away; nothing to report from here */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    // ================= the tailer, against real files =================

    private static void CheckTailer()
    {
        Section("Tailer");

        var dir = Path.Combine(Path.GetTempPath(), "loglens-tailer-tests");
        Directory.CreateDirectory(dir);

        CheckCrlfAcrossReadBoundary(dir);
        CheckTruncateAndRewrite(dir);
        CheckMultiByteAcrossReadBoundary(dir);
        CheckPartialLineHeldBack(dir);
        CheckWildcardRoll(dir);
        CheckRenameRotation(dir);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The decoder is kept across reads precisely so a multi-byte character cut in
    /// half by the 64 KB chunking survives. Here the three bytes of "€" sit at offsets
    /// 65535–65537: the first in one read, the other two in the next. A per-read
    /// decoder would emit replacement characters on both sides of the cut.
    /// </summary>
    private static void CheckMultiByteAcrossReadBoundary(string dir)
    {
        var path = Path.Combine(dir, "utf8-boundary.log");
        File.WriteAllText(path, new string('x', 65535) + "€\n" + "naïve café\n", new UTF8Encoding(false));

        var lines = Collect(path, TimeSpan.FromSeconds(2), expected: 2, out var error);

        bool ok = lines.Count == 2
                  && lines[0].Length == 65536 && lines[0].EndsWith('€')
                  && lines[1] == "naïve café"
                  && !lines.Any(l => l.Contains('�'));

        Report(ok, "a UTF-8 character split across the 64 KB read boundary decodes intact",
            $"got {lines.Count} lines, first ends '{(lines.Count > 0 ? lines[0][^Math.Min(3, lines[0].Length)..] : "")}', "
            + $"error={error ?? "none"}");
    }

    /// <summary>
    /// A writer mid-flush leaves a line without its newline. Emitting it would show a
    /// half line that then "changes" — so it must wait for the rest, and then arrive
    /// as one line, not as the fragment plus the remainder.
    /// </summary>
    private static void CheckPartialLineHeldBack(string dir)
    {
        var path = Path.Combine(dir, "partial.log");
        File.WriteAllText(path, "complete\npart", new UTF8Encoding(false));

        var got = new List<string>();
        var tailer = new LogTailer(path, 0);
        tailer.Batch += b => { lock (got) got.AddRange(b.Lines); };
        tailer.Start(40);

        bool heldBack;
        try
        {
            WaitFor(() => { lock (got) return got.Contains("complete"); }, TimeSpan.FromSeconds(2));

            // Several polls with the fragment sitting there unfinished. A sleep is the
            // honest tool here: the check is that something does NOT happen.
            Thread.Sleep(300);
            lock (got) heldBack = got.Count == 1;

            File.AppendAllText(path, "ial\n");
            WaitFor(() => { lock (got) return got.Count >= 2; }, TimeSpan.FromSeconds(2));
        }
        finally { tailer.Dispose(); }

        List<string> snapshot;
        lock (got) snapshot = got.ToList();

        Report(heldBack && snapshot.SequenceEqual(["complete", "partial"]),
            "a trailing partial line is held back until its newline arrives, then emitted whole",
            $"heldBack={heldBack}, lines=[{string.Join(", ", snapshot)}]");
    }

    /// <summary>
    /// A wildcard spec follows a rolling logger onto the newest file. The roll must
    /// arrive as a Rewound batch — that is what tells the tab and the merged view to
    /// drop the old file's lines — carrying only the new file's content.
    /// </summary>
    private static void CheckWildcardRoll(string dir)
    {
        var rollDir = Path.Combine(dir, "roll");
        if (Directory.Exists(rollDir)) Directory.Delete(rollDir, true);   // a previous aborted run
        Directory.CreateDirectory(rollDir);

        var day1 = Path.Combine(rollDir, "app-20260814.log");
        File.WriteAllText(day1, "old-1\nold-2\n");
        // Newest-by-write-time decides the match; keep the clock's resolution out of it.
        File.SetLastWriteTimeUtc(day1, DateTime.UtcNow.AddHours(-1));

        var batches = new List<TailBatch>();
        var tailer = new LogTailer(Path.Combine(rollDir, "app-*.log"), 0);
        tailer.Batch += b => { lock (batches) batches.Add(b); };
        tailer.Start(40);

        try
        {
            WaitFor(() => { lock (batches) return batches.Any(b => b.Lines.Contains("old-2")); },
                TimeSpan.FromSeconds(2));

            // Written under a name the pattern ignores, then moved in, so the roll is
            // seen with its content rather than as an empty file that fills later.
            var staging = Path.Combine(rollDir, "app-20260815.tmp");
            File.WriteAllText(staging, "new-1\nnew-2\n");
            File.Move(staging, Path.Combine(rollDir, "app-20260815.log"));

            WaitFor(() => { lock (batches) return batches.Any(b => b.Lines.Contains("new-2")); },
                TimeSpan.FromSeconds(3));
        }
        finally { tailer.Dispose(); }

        List<TailBatch> snapshot;
        lock (batches) snapshot = batches.ToList();

        int rollAt = snapshot.FindIndex(b => b.Rewound && Path.GetFileName(b.ResolvedPath) == "app-20260815.log");
        var afterRoll = rollAt < 0 ? new List<string>() : snapshot.Skip(rollAt).SelectMany(b => b.Lines).ToList();

        Report(rollAt >= 0 && afterRoll.SequenceEqual(["new-1", "new-2"]),
            "a wildcard spec rolls onto the newer file with a Rewound batch of only its lines",
            $"rewound batch at {rollAt}, lines after roll=[{string.Join(", ", afterRoll)}]");
    }

    /// <summary>
    /// Rename-rotation (log4net RollingFileAppender, NLog archiving): the live file is
    /// renamed away and a fresh one created at the same path. The tailer reopens the
    /// path every poll, so it sees the new, shorter file and must start it from the
    /// top instead of seeking past its end.
    ///
    /// Known limit, not checked here: with no file identity to compare, a new file
    /// that has already grown PAST the old read position before the next poll is
    /// read from that position as if it were the old file continuing.
    /// </summary>
    private static void CheckRenameRotation(string dir)
    {
        var path = Path.Combine(dir, "renamed.log");
        File.Delete(path + ".1");   // a previous aborted run
        File.WriteAllText(path, "before-1 padding padding padding\nbefore-2 padding padding padding\n");

        var batches = new List<TailBatch>();
        var tailer = new LogTailer(path, 0);
        tailer.Batch += b => { lock (batches) batches.Add(b); };
        tailer.Start(40);

        string? lastError = null;
        try
        {
            WaitFor(() => { lock (batches) return batches.Any(b => b.Lines.Any(l => l.StartsWith("before-2"))); },
                TimeSpan.FromSeconds(2));

            File.Move(path, path + ".1");
            File.WriteAllText(path, "after-1\n");

            WaitFor(() => { lock (batches) return batches.Any(b => b.Lines.Contains("after-1")); },
                TimeSpan.FromSeconds(3));
            lastError = tailer.LastError;
        }
        finally { tailer.Dispose(); }

        List<TailBatch> snapshot;
        lock (batches) snapshot = batches.ToList();

        // The first batch is the initial read (also Rewound); look for the next one.
        int rotatedAt = snapshot.Count < 2 ? -1 : snapshot.FindIndex(1, b => b.Rewound);
        var afterRotation = rotatedAt < 0 ? new List<string>() : snapshot.Skip(rotatedAt).SelectMany(b => b.Lines).ToList();

        Report(rotatedAt > 0 && afterRotation.SequenceEqual(["after-1"]) && lastError is null,
            "a file renamed away and recreated at the same path is re-read from the top",
            $"rewound at {rotatedAt}, lines after=[{string.Join(", ", afterRotation)}], lastError={lastError ?? "none"}");
    }

    /// <summary>
    /// The tailer reads in 64 KB chunks. This file is built so that a \r sits on the
    /// very last byte of the first chunk and its \n opens the second, which used to
    /// be emitted as an extra blank line.
    /// </summary>
    private static void CheckCrlfAcrossReadBoundary(string dir)
    {
        var path = Path.Combine(dir, "crlf-boundary.log");

        // 65535 bytes of filler puts the \r at byte offset 65535 — the last byte the
        // first 65536-byte read consumes.
        var content = new string('x', 65535) + "\r\n" + "second line\r\n" + "third line\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var lines = Collect(path, TimeSpan.FromSeconds(2), expected: 3, out var error);

        bool noBlanks = lines.All(l => l.Length > 0);
        bool rightCount = lines.Count == 3;
        bool rightOrder = rightCount
                          && lines[0].Length == 65535
                          && lines[1] == "second line"
                          && lines[2] == "third line";

        Report(noBlanks && rightOrder,
            "a CRLF split across the 64 KB read boundary does not emit a blank line",
            $"got {lines.Count} lines, blanks={lines.Count(l => l.Length == 0)}, error={error ?? "none"}");
    }

    /// <summary>
    /// Truncate-and-rewrite is the logrotate copytruncate pattern. It used to discard
    /// the decoder without clearing the primed flag, so the next read dereferenced null.
    /// </summary>
    private static void CheckTruncateAndRewrite(string dir)
    {
        var path = Path.Combine(dir, "rotate.log");
        File.WriteAllText(path, "one\r\ntwo\r\n", new UTF8Encoding(false));

        var got = new List<string>();
        string? lastError = null;

        var tailer = new LogTailer(path, 0);
        tailer.Batch += b => { lock (got) got.AddRange(b.Lines); };
        tailer.Start(40);

        try
        {
            WaitFor(() => { lock (got) return got.Count >= 2; }, TimeSpan.FromSeconds(2));

            // Rewrite smaller than before, which is what makes the tailer see a truncation.
            File.WriteAllText(path, "fresh\r\n", new UTF8Encoding(false));

            WaitFor(() => { lock (got) return got.Contains("fresh"); }, TimeSpan.FromSeconds(3));
            lastError = tailer.LastError;
        }
        finally { tailer.Dispose(); }

        bool sawFresh;
        lock (got) sawFresh = got.Contains("fresh");

        Report(sawFresh && lastError is null,
            "a truncated-and-rewritten file keeps tailing instead of throwing",
            $"sawFresh={sawFresh}, lastError={lastError ?? "none"}, lines=[{string.Join(", ", got)}]");
    }

    private static List<string> Collect(string path, TimeSpan timeout, int expected, out string? error)
    {
        var got = new List<string>();
        var tailer = new LogTailer(path, 0);
        tailer.Batch += b => { lock (got) got.AddRange(b.Lines); };
        tailer.Start(40);

        try
        {
            WaitFor(() => { lock (got) return got.Count >= expected; }, timeout);
            error = tailer.LastError;
        }
        finally { tailer.Dispose(); }

        lock (got) return got.ToList();
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
    }

    private static List<HighlightRule> Preset(string name)
    {
        var p = RulePresets.All.FirstOrDefault(x => x.Name == name)
                ?? throw new InvalidOperationException($"No preset named '{name}'");
        return p.Build();
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Expect(RuleSet set, string line, Severity expected, string what)
    {
        var rule = set.Match(line);
        var actual = rule?.Severity ?? Severity.None;
        Report(actual == expected, what, $"expected {expected}, got {actual} (rule: {rule?.Name ?? "none"})");
    }

    private static void ExpectRule(RuleSet set, string line, string expectedRule, Severity expectedSeverity, string what)
    {
        var rule = set.Match(line);
        bool ok = rule?.Name == expectedRule && rule.Severity == expectedSeverity;
        Report(ok, what, $"expected rule '{expectedRule}'/{expectedSeverity}, got '{rule?.Name ?? "none"}'/{rule?.Severity ?? Severity.None}");
    }

    /// <summary>
    /// For checks that depend on the machine rather than on our code. A skip is
    /// reported loudly but does not fail the run.
    /// </summary>
    private static void Skip(string what, string why)
    {
        _skipped++;
        Console.WriteLine($"  SKIP  {what}");
        Console.WriteLine($"        {why}");
    }

    private static void Report(bool ok, string what, string detail)
    {
        if (ok)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}");
        Console.WriteLine($"        {detail}");
    }
}
