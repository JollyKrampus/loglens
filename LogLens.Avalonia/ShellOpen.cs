using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace LogLens.Avalonia;

/// <summary>
/// The macOS (and Linux) twin of the WPF app's ShellOpen: open a log in an editor,
/// or show it in the file manager. Same contract — everything returns an error
/// string rather than throwing, because these run from clicks where an exception
/// would only become a crash.
///
/// On macOS this is <c>open -t</c> and <c>open -R</c>. <c>-t</c> rather than a plain
/// <c>open</c> on purpose: macOS associates .log with Console.app, a log viewer, and
/// the action the user picked says "editor" — <c>-t</c> is the user's default text
/// editor (TextEdit unless they changed it). Linux has no equivalent split, so it is
/// <c>xdg-open</c> on the file, and on its folder to reveal (xdg-open cannot select).
///
/// Arguments always go through <see cref="ProcessStartInfo.ArgumentList"/>, never a
/// command string, and paths are made absolute first so a relative path that happens
/// to start with '-' can't be read as an option.
/// </summary>
public static class ShellOpen
{
    /// <summary>The menu wording for "reveal", in the platform's own vocabulary.</summary>
    public static string RevealMenuHeader { get; } =
        OperatingSystem.IsMacOS() ? "Show log in Finder"
        : OperatingSystem.IsWindows() ? "Show log in File Explorer"
        : "Open the log's folder";

    /// <summary>Main-menu wording, which says "current log" like the WPF menu.</summary>
    public static string RevealCurrentMenuHeader { get; } =
        OperatingSystem.IsMacOS() ? "Show current log in _Finder"
        : OperatingSystem.IsWindows() ? "Show current log in File E_xplorer"
        : "Open current log's _folder";

    public static string? OpenInEditor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No file to open.";
        if (!File.Exists(path)) return $"File not found: {path}";

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return "Could not open the file: " + ex.Message; }

        if (OperatingSystem.IsMacOS()) return Run("open", "-t", full);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
                return null;
            }
            catch (Exception ex) { return "Could not open the file: " + ex.Message; }
        }

        return Run("xdg-open", full);
    }

    public static string? Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No file to show.";

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return "Could not show the file: " + ex.Message; }

        // The file may have rotated away; showing its folder still helps.
        bool fileExists = File.Exists(full);
        var dir = Path.GetDirectoryName(full);
        if (!fileExists && (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)))
            return $"File not found: {path}";

        if (OperatingSystem.IsMacOS())
            return fileExists ? Run("open", "-R", full) : Run("open", dir!);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = fileExists
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + full + "\"")
                    : new ProcessStartInfo("explorer.exe", "\"" + dir + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi)?.Dispose();
                return null;
            }
            catch (Exception ex) { return "Could not open File Explorer: " + ex.Message; }
        }

        return Run("xdg-open", dir!);
    }

    /// <summary>
    /// Starts a helper and reports a non-zero exit. <c>open</c> returns as soon as
    /// LaunchServices has the request; <c>xdg-open</c> can block for the life of a
    /// terminal editor, so a helper still running after a few seconds counts as
    /// success rather than a hang. The callers run this off the UI thread.
    ///
    /// Output is deliberately not redirected: the launched editor inherits the
    /// helper's stdio, and a pipe this process later closed could kill it on its
    /// first write.
    /// </summary>
    private static string? Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return $"Could not start {exe}.";
            if (!p.WaitForExit(5000)) return null;

            return p.ExitCode == 0 ? null : $"{exe} could not open it (exit code {p.ExitCode}).";
        }
        catch (Win32Exception) { return $"{exe} is not available on this system."; }
        catch (Exception ex) { return $"Could not run {exe}: {ex.Message}"; }
    }
}
