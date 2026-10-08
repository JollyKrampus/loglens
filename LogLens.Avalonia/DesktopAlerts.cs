using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Avalonia;

/// <summary>
/// The macOS (and Linux) delivery for alerts — the twin of the WPF AlertService's
/// balloon and .wav. Whether to alert at all is <see cref="AlertPolicy"/>'s decision,
/// shared with Windows; this class only makes the noise.
///
/// macOS: a Notification Center banner via <c>osascript</c>, and a system sound via
/// <c>afplay</c>. Both ship with every macOS, need no entitlement or signing, and
/// cost nothing when unused — a native UserNotifications binding would need a signed,
/// identified bundle, which an unsigned .app cleared with <c>xattr -cr</c> is not.
/// Linux: <c>notify-send</c> if installed; no sound.
///
/// The notification text comes from the log, so it is an injection surface. It is
/// never interpolated into AppleScript: the script is a constant and the text is
/// passed as argv, which AppleScript receives as plain strings. The script reads the
/// last three arguments (negative indexes) so it is correct whether or not osascript
/// passes the "--" separator through. Everything runs on the thread pool and
/// swallows its own failures — an alert must never disturb tailing.
///
/// Known limit: a banner posted through osascript belongs to Script Editor, so
/// clicking it opens Script Editor rather than jumping to the view (the WPF balloon
/// does jump). Doing better needs the signed bundle mentioned above.
/// </summary>
public sealed class DesktopAlerts
{
    private readonly AlertSettings _settings;

    public DesktopAlerts(AlertSettings settings)
    {
        _settings = settings;
        Policy = new AlertPolicy(settings);
    }

    public AlertPolicy Policy { get; }

    public static bool CanNotify => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Only macOS has a system sound set we can name portably.</summary>
    public static bool CanPlaySound => OperatingSystem.IsMacOS();

    /// <summary>Considers a batch of newly ingested lines. Returns true if it alerted.</summary>
    public bool Consider(bool appInForeground, string viewName, bool viewAlertsEnabled,
                         string tabName, IReadOnlyList<LogLine> lines)
    {
        var outcome = Policy.Decide(appInForeground, viewName, viewAlertsEnabled,
                                    lines, out var trigger, out int matchCount);
        if (outcome != AlertOutcome.Alerted || trigger is null) return false;

        Deliver(viewName, tabName, trigger, matchCount);
        return true;
    }

    /// <summary>Fires a sample so you can confirm it reaches you. Bypasses throttle and focus.</summary>
    public void SendTestAlert() => Deliver("Test", "alert preview", AlertPolicy.SampleLine(), 1);

    private void Deliver(string viewName, string tabName, LogLine line, int count)
    {
        // Read settings on the caller's thread; the pool thread only spawns processes.
        bool toast = _settings.ShowToast && CanNotify;
        bool sound = _settings.PlaySound && CanPlaySound;
        if (!toast && !sound) return;

        var title = AlertPolicy.Title(viewName, line, count);
        var body = AlertPolicy.Body(line);
        var soundName = _settings.MacSoundFor(line.Severity);

        Task.Run(() =>
        {
            if (sound) PlaySound(soundName);
            if (toast) Notify(title, tabName, body);
        });
    }

    // ---- notification --------------------------------------------------------------

    private static void Notify(string title, string subtitle, string body)
    {
        title = OneLine(title);
        subtitle = OneLine(subtitle);
        body = OneLine(body);

        if (OperatingSystem.IsMacOS())
        {
            Spawn("/usr/bin/osascript",
                "-e", "on run argv",
                "-e", "display notification (item -3 of argv) with title (item -2 of argv) subtitle (item -1 of argv)",
                "-e", "end run",
                "--", body, title, subtitle);
        }
        else if (OperatingSystem.IsLinux())
        {
            Spawn("notify-send", "--app-name=LogLens", "--", title, subtitle + "\n" + body);
        }
    }

    /// <summary>
    /// Control characters out (a NUL can't cross argv at all, and newlines make a
    /// banner unreadable), length capped.
    /// </summary>
    private static string OneLine(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 256));
        foreach (var c in s)
        {
            if (sb.Length >= 256) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    // ---- sound -----------------------------------------------------------------------

    /// <summary>
    /// Plays a macOS system sound by name. The name comes from a workspace a teammate
    /// may have edited by hand, so only names in <see cref="MacSounds.All"/> are
    /// accepted — never an arbitrary path — and anything else falls back to the default.
    /// </summary>
    public static void PlaySound(string? name)
    {
        if (!CanPlaySound) return;
        var path = MacSounds.PathFor(name);
        if (path is null) return;
        Spawn("/usr/bin/afplay", path);     // not awaited: afplay plays on its own
    }

    private static void Spawn(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
        }
        catch (Win32Exception) { /* helper not installed: alerts degrade to nothing */ }
        catch (Exception ex) { Debug.WriteLine("LogLens alert: " + ex.Message); }
    }
}

/// <summary>The macOS system sounds offered in the Alerts dialog.</summary>
public static class MacSounds
{
    public const string Folder = "/System/Library/Sounds";
    public const string DefaultSound = "Glass";
    public const string DefaultFatalSound = "Basso";

    /// <summary>The set every macOS since 10.x ships, in /System/Library/Sounds.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Basso", "Blow", "Bottle", "Frog", "Funk", "Glass", "Hero",
        "Morse", "Ping", "Pop", "Purr", "Sosumi", "Submarine", "Tink"
    ];

    /// <summary>Canonical name from the list (case-insensitive), or the default.</summary>
    public static string Normalize(string? name, string fallback = DefaultSound)
    {
        foreach (var s in All)
            if (string.Equals(s, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return fallback;
    }

    /// <summary>The .aiff to play, or null if the file isn't there (degrade to silence).</summary>
    public static string? PathFor(string? name)
    {
        var path = Path.Combine(Folder, Normalize(name) + ".aiff");
        return File.Exists(path) ? path : null;
    }
}
