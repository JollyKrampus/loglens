using System.Text.RegularExpressions;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>Why a batch did or didn't raise an alert. Exposed so it can be tested.</summary>
public enum AlertOutcome
{
    Alerted,
    Disabled,
    ViewMuted,
    AppInForeground,
    NothingMatched,
    Throttled
}

/// <summary>
/// Whether a batch of new lines deserves an alert, and what the alert should say.
///
/// This used to live inside the WPF app's AlertService, beside the tray balloon and
/// the taskbar flash. None of it was Windows-specific — thresholds, the per-view
/// mute, the foreground check, the custom pattern and the throttle are all plain
/// logic — so it moved here when the macOS app gained alerts, and both shells now
/// ask the same object the same question. Only the *delivery* (balloon, flash and
/// .wav on Windows; osascript and afplay on macOS) stays per shell. Two copies of
/// the rules would have drifted the first time someone tuned one of them.
///
/// Throttling is per view and stateful, so a shell keeps one instance for its
/// lifetime rather than constructing one per batch.
/// </summary>
public sealed class AlertPolicy
{
    private readonly AlertSettings _settings;
    private readonly Dictionary<string, long> _lastAlertPerView = new();

    private Regex? _customRegex;
    private string _customSource = "";

    public AlertPolicy(AlertSettings settings) => _settings = settings;

    public AlertSettings Settings => _settings;

    /// <summary>
    /// The whole decision, with no platform dependencies, so the rules can be tested
    /// without a message pump, a tray icon or a Mac. Each shell's alert service is
    /// the thin wrapper that actually makes noise.
    /// </summary>
    public AlertOutcome Decide(bool appInForeground, string viewName, bool viewAlertsEnabled,
                               IReadOnlyList<LogLine> lines, out LogLine? trigger, out int matchCount)
    {
        trigger = null;
        matchCount = 0;

        if (!_settings.Enabled) return AlertOutcome.Disabled;
        if (!viewAlertsEnabled) return AlertOutcome.ViewMuted;
        if (lines.Count == 0) return AlertOutcome.NothingMatched;
        if (_settings.OnlyWhenUnfocused && appInForeground) return AlertOutcome.AppInForeground;

        trigger = FindTrigger(lines, out matchCount);
        if (trigger is null) return AlertOutcome.NothingMatched;

        // Throttle last, so merely looking at a batch never consumes the budget.
        if (!PassesThrottle(viewName)) return AlertOutcome.Throttled;

        return AlertOutcome.Alerted;
    }

    /// <summary>
    /// "Prod — 3 new errors" or "Prod — fatal". Shared so the two shells word the
    /// same alert the same way.
    /// </summary>
    public static string Title(string viewName, LogLine line, int count)
    {
        var level = line.Severity.ToString().ToLowerInvariant();
        return count > 1 ? $"{viewName} — {count} new {level}s" : $"{viewName} — {level}";
    }

    /// <summary>
    /// The triggering line, capped. A notification is a pointer back into the app,
    /// not a place to read a 4 KB JSON event.
    /// </summary>
    public static string Body(LogLine line, int maxChars = 220)
        => line.Text.Length > maxChars ? line.Text[..maxChars] + "…" : line.Text;

    /// <summary>The line a "Send a test alert" shows, identical on both platforms.</summary>
    public static LogLine SampleLine()
    {
        var now = DateTime.Now;
        return new LogLine(1,
            $"{now:yyyy-MM-dd HH:mm:ss.fff}|ERROR|LogLens.Test|This is what an alert looks like",
            null, now, "test");
    }

    private LogLine? FindTrigger(IReadOnlyList<LogLine> lines, out int matchCount)
    {
        matchCount = 0;
        LogLine? first = null;

        var rx = GetCustomRegex();

        foreach (var line in lines)
        {
            bool hit = line.Severity != Severity.None && line.Severity >= _settings.MinimumSeverity;

            if (!hit && rx is not null)
            {
                try { hit = rx.IsMatch(line.Text); }
                catch (RegexMatchTimeoutException) { hit = false; }
            }

            if (!hit) continue;
            matchCount++;
            first ??= line;
        }

        return first;
    }

    private Regex? GetCustomRegex()
    {
        var pattern = _settings.CustomPattern?.Trim() ?? "";
        if (pattern.Length == 0) return null;

        if (!string.Equals(pattern, _customSource, StringComparison.Ordinal))
        {
            _customSource = pattern;
            try
            {
                _customRegex = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(100));
            }
            catch { _customRegex = null; }
        }

        return _customRegex;
    }

    private bool PassesThrottle(string viewName)
    {
        long now = Environment.TickCount64;
        long window = _settings.ThrottleSeconds * 1000L;

        if (_lastAlertPerView.TryGetValue(viewName, out var last) && now - last < window)
            return false;

        _lastAlertPerView[viewName] = now;
        return true;
    }
}
