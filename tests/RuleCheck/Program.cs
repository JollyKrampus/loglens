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
            Severity.Error, "generic keyword preset mis-reads that line (documented trade-off)");

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
        CheckTailer();

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
