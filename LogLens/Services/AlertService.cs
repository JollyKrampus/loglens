using System.Runtime.InteropServices;
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
/// started producing hundreds of errors a second. That decision is
/// <see cref="AlertPolicy"/>'s, in LogLens.Core, so the macOS app applies the very
/// same rules; this class only delivers.
/// </summary>
public sealed class AlertService : IDisposable
{
    private readonly AlertSettings _settings;
    private readonly AlertPolicy _policy;

    private System.Windows.Forms.NotifyIcon? _tray;

    public AlertService(AlertSettings settings)
    {
        _settings = settings;
        _policy = new AlertPolicy(settings);
    }

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

    /// <summary>
    /// The decision itself — thresholds, per-view mute, focus, custom pattern,
    /// throttle — lives in <see cref="AlertPolicy"/> in LogLens.Core, shared with the
    /// macOS app. This class is only the Windows delivery: balloon, flash, .wav.
    /// </summary>
    public AlertPolicy Policy => _policy;

    /// <summary>
    /// Considers a batch of newly ingested lines. Returns true if it actually alerted.
    /// </summary>
    public bool Consider(Window? owner, string viewName, bool viewAlertsEnabled,
                         string tabName, IReadOnlyList<LogLine> lines)
    {
        var outcome = _policy.Decide(IsForeground(owner), viewName, viewAlertsEnabled,
                                     lines, out var trigger, out int matchCount);

        if (outcome != AlertOutcome.Alerted || trigger is null) return false;

        Notify(owner, viewName, tabName, trigger, matchCount);
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

            _tray.Tag = viewName;
            _tray.BalloonTipTitle = AlertPolicy.Title(viewName, line, count);
            _tray.BalloonTipText = $"{tabName}\n{AlertPolicy.Body(line)}";
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
        var line = AlertPolicy.SampleLine();

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
