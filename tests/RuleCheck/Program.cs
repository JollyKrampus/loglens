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
        CheckWorkspaceCompat();
        CheckLegacyRuleUpgrade();
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

        // Fresh service per case so throttling from one doesn't leak into the next.
        AlertService New(Action<AlertSettings>? tweak = null)
        {
            var s = new AlertSettings { ThrottleSeconds = 0 };
            tweak?.Invoke(s);
            return new AlertService(s);
        }

        Outcome(New(), false, "Prod", true, errorBatch,
            AlertService.AlertOutcome.Alerted, "an ERROR alerts");

        Outcome(New(), false, "Prod", true, fatalBatch,
            AlertService.AlertOutcome.Alerted, "a FATAL alerts");

        Outcome(New(), false, "Prod", true, warnBatch,
            AlertService.AlertOutcome.NothingMatched, "a WARN does not alert at the default Error threshold");

        Outcome(New(s => s.MinimumSeverity = Severity.Warn), false, "Prod", true, warnBatch,
            AlertService.AlertOutcome.Alerted, "a WARN alerts once the threshold is lowered");

        Outcome(New(), false, "Prod", true, quietBatch,
            AlertService.AlertOutcome.NothingMatched, "a clean batch does not alert");

        Outcome(New(s => s.Enabled = false), false, "Prod", true, errorBatch,
            AlertService.AlertOutcome.Disabled, "alerts off globally suppresses everything");

        Outcome(New(), false, "Dev", false, errorBatch,
            AlertService.AlertOutcome.ViewMuted, "a muted view stays quiet");

        Outcome(New(), true, "Prod", true, errorBatch,
            AlertService.AlertOutcome.AppInForeground, "no alert while you are looking at the app");

        Outcome(New(s => s.OnlyWhenUnfocused = false), true, "Prod", true, errorBatch,
            AlertService.AlertOutcome.Alerted, "unless that check is turned off");

        // Custom pattern fires regardless of severity.
        Outcome(New(s => s.CustomPattern = "ORDER-9[0-9]{3}"), false, "Prod", true,
            new[] { L("INFO reconciled ORDER-9042", null) },
            AlertService.AlertOutcome.Alerted, "a custom pattern alerts on an INFO line");

        Outcome(New(s => s.CustomPattern = "ORDER-9[0-9]{3}"), false, "Prod", true,
            new[] { L("INFO reconciled ORDER-1042", null) },
            AlertService.AlertOutcome.NothingMatched, "a custom pattern that misses stays quiet");

        // An invalid custom regex must be inert, never fatal.
        Outcome(New(s => s.CustomPattern = "([unclosed"), false, "Prod", true, quietBatch,
            AlertService.AlertOutcome.NothingMatched, "an invalid custom pattern is inert, not a crash");

        // Throttling: the second batch inside the window is suppressed.
        var throttled = New(s => s.ThrottleSeconds = 60);
        var first = throttled.Decide(false, "Prod", true, errorBatch, out _, out _);
        var second = throttled.Decide(false, "Prod", true, errorBatch, out _, out _);
        Report(first == AlertService.AlertOutcome.Alerted && second == AlertService.AlertOutcome.Throttled,
            "a log storm produces one alert, not thousands",
            $"first={first}, second={second}");

        // ...but a different view has its own budget.
        var other = throttled.Decide(false, "Test", true, errorBatch, out _, out _);
        Report(other == AlertService.AlertOutcome.Alerted,
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
    }

    private static void Outcome(AlertService svc, bool inForeground, string view, bool viewEnabled,
                                IReadOnlyList<LogLine> lines,
                                AlertService.AlertOutcome expected, string what)
    {
        var actual = svc.Decide(inForeground, view, viewEnabled, lines, out _, out _);
        Report(actual == expected, what, $"expected {expected}, got {actual}");
        svc.Dispose();
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
                   && !again.Settings.AutoSaveWorkspace,
                "severity chips, filters and auto-save round-trip through the workspace file",
                $"info={paneAgain.ShowInfo} include='{paneAgain.Include}' mergedWarn={again.Views[0].MergedPane.ShowWarn}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
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

    private static void CheckLegacyRuleUpgrade()
    {
        Section("Legacy default-rule upgrade");

        var path = Path.Combine(Path.GetTempPath(), $"loglens-rules-{Guid.NewGuid():N}.json");
        try
        {
            // A workspace still carrying the untouched pre-1.5.1 defaults, written by
            // the real serialiser, must come back with the two-tier defaults.
            var old = Workspace.CreateDefault();
            old.Rules = Pre151DefaultRules();
            WorkspaceStore.Save(old, path);

            var upgraded = WorkspaceStore.Load(path);
            var fresh = HighlightRule.Defaults();

            Report(upgraded.Rules.Count == fresh.Count
                   && upgraded.Rules[0].Name == fresh[0].Name
                   && upgraded.Rules[0].Pattern == fresh[0].Pattern,
                "untouched pre-1.5.1 defaults are upgraded to the two-tier defaults",
                $"count={upgraded.Rules.Count} first={upgraded.Rules[0].Name}");

            var set = new RuleSet([], upgraded.Rules);
            var match = set.Match("2026-08-18 07:12:44.1230|Error|Acme.Gateway|****Fatal error received from gateway");
            Report(match?.Severity == Severity.Error,
                "after the upgrade, |Error| beats a message mentioning 'Fatal'",
                $"got {match?.Severity.ToString() ?? "none"} via '{match?.Name ?? "none"}'");

            // One recoloured rule = the user owns the list; nothing changes.
            var custom = Workspace.CreateDefault();
            custom.Rules = Pre151DefaultRules();
            custom.Rules[1].Foreground = "#FF0000";
            WorkspaceStore.Save(custom, path);

            var kept = WorkspaceStore.Load(path);
            Report(kept.Rules.Count == 7 && kept.Rules[1].Foreground == "#FF0000",
                "a customised rule set is left exactly alone",
                $"count={kept.Rules.Count} fg={kept.Rules[1].Foreground}");

            // Same for a disabled rule — that is a deliberate choice, not staleness.
            var muted = Workspace.CreateDefault();
            muted.Rules = Pre151DefaultRules();
            muted.Rules[4].Enabled = false;
            WorkspaceStore.Save(muted, path);

            var keptMuted = WorkspaceStore.Load(path);
            Report(keptMuted.Rules.Count == 7 && !keptMuted.Rules[4].Enabled,
                "a rule set with a disabled rule is left alone too",
                $"count={keptMuted.Rules.Count}");

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

    // ================= the tailer, against real files =================

    private static void CheckTailer()
    {
        Section("Tailer");

        var dir = Path.Combine(Path.GetTempPath(), "loglens-tailer-tests");
        Directory.CreateDirectory(dir);

        CheckCrlfAcrossReadBoundary(dir);
        CheckTruncateAndRewrite(dir);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
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
