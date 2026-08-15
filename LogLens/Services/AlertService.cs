using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using LogLens.Models;
using LogLens.ViewModels;

namespace LogLens.Services;

/// <summary>
/// Tells you something broke while you were looking somewhere else.
///
/// Uses a tray-icon balloon rather than a WinRT toast on purpose: WinRT toasts
/// require a registered AppUserModelID and a packaged identity, which a portable
/// single-exe you copy onto a jump box does not have. A NotifyIcon balloon renders
/// as a normal notification on Windows 10 and 11 with no registration at all.
///
/// Everything is throttled per view, because the whole point is a log that has just
/// started producing hundreds of errors a second.
/// </summary>
public sealed class AlertService : IDisposable
{
    private readonly AlertSettings _settings;
    private readonly Dictionary<string, long> _lastAlertPerView = new();

    private System.Windows.Forms.NotifyIcon? _tray;
    private Regex? _customRegex;
    private string _customSource = "";

    public AlertService(AlertSettings settings) => _settings = settings;

    /// <summary>Raised when the user clicks the notification, so the shell can focus that view.</summary>
    public event Action<string>? AlertActivated;

    // ---- native ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    // ---- entry point --------------------------------------------------------------

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
    /// The whole decision, with no Windows dependencies, so the rules can be tested
    /// without a message pump or a tray icon. <see cref="Consider"/> is the thin
    /// wrapper that actually makes noise.
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
    /// Considers a batch of newly ingested lines. Returns true if it actually alerted.
    /// </summary>
    public bool Consider(Window? owner, string viewName, bool viewAlertsEnabled,
                         string tabName, IReadOnlyList<LogLine> lines)
    {
        var outcome = Decide(IsForeground(owner), viewName, viewAlertsEnabled,
                             lines, out var trigger, out int matchCount);

        if (outcome != AlertOutcome.Alerted || trigger is null) return false;

        Notify(owner, viewName, tabName, trigger, matchCount);
        return true;
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

    private static bool IsForeground(Window? owner)
    {
        if (owner is null) return false;
        try
        {
            var handle = new WindowInteropHelper(owner).Handle;
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }
        catch { return false; }
    }

    // ---- output -------------------------------------------------------------------

    private void Notify(Window? owner, string viewName, string tabName, LogLine line, int count)
    {
        if (_settings.PlaySound) SoundLibrary.Play(_settings.SoundFor(line.Severity));

        if (_settings.FlashTaskbar && owner is not null) Flash(owner);

        if (_settings.ShowToast) ShowBalloon(viewName, tabName, line, count);
    }

    private static void Flash(Window owner)
    {
        try
        {
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle == IntPtr.Zero) return;

            var info = new FLASHWINFO
            {
                hwnd = handle,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0
            };
            info.cbSize = (uint)Marshal.SizeOf(info);
            FlashWindowEx(ref info);
        }
        catch { /* cosmetic only */ }
    }

    private void ShowBalloon(string viewName, string tabName, LogLine line, int count)
    {
        try
        {
            _tray ??= CreateTray();
            if (_tray is null) return;

            var title = count > 1
                ? $"{viewName} — {count} new {line.Severity.ToString().ToLowerInvariant()}s"
                : $"{viewName} — {line.Severity.ToString().ToLowerInvariant()}";

            var body = line.Text.Length > 220 ? line.Text[..220] + "…" : line.Text;

            _tray.Tag = viewName;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = $"{tabName}\n{body}";
            _tray.BalloonTipIcon = line.Severity == Severity.Fatal
                ? System.Windows.Forms.ToolTipIcon.Error
                : System.Windows.Forms.ToolTipIcon.Warning;
            _tray.Visible = true;
            _tray.ShowBalloonTip(5000);
        }
        catch { /* notifications are best-effort */ }
    }

    private System.Windows.Forms.NotifyIcon? CreateTray()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                       ?? System.Drawing.SystemIcons.Information;

            var tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "LogLens",
                Visible = false
            };

            tray.BalloonTipClicked += (s, _) =>
            {
                if ((s as System.Windows.Forms.NotifyIcon)?.Tag is string v) AlertActivated?.Invoke(v);
            };

            return tray;
        }
        catch { return null; }
    }

    /// <summary>Fires a sample notification so you can confirm it actually reaches you.</summary>
    public void SendTestAlert(Window? owner)
    {
        var line = new LogLine(1,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|ERROR|LogLens.Test|This is what an alert looks like",
            null, DateTime.Now, "test");

        // Bypass throttling and the focus check — the user explicitly asked for this.
        Notify(owner, "Test", "alert preview", line, 1);
    }

    public void Dispose()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }
}
