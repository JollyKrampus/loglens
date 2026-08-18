using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace LogLens.Services;

/// <summary>
/// Opens a log file in whatever the user considers an editor. Everything returns an
/// error string rather than throwing, because these run from button clicks where an
/// exception would just become a crash dialog.
/// </summary>
public static class ShellOpen
{
    /// <summary>
    /// Opens with the shell's associated application. Machines with no association
    /// for .log (common on fresh installs) fall back to Notepad rather than failing.
    /// </summary>
    public static string? OpenInEditor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No file to open.";
        if (!File.Exists(path)) return $"File not found: {path}";

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex)
        {
            // Win32Exception covers more than "no association": a file deleted
            // between the check above and the launch throws the same type with
            // ERROR_FILE_NOT_FOUND, and falling back to Notepad then opens a blank
            // editor on a path that no longer exists. Only the two documented
            // no-association codes justify the fallback.
            const int SE_ERR_NOASSOC = 31;
            const int ERROR_NO_ASSOCIATION = 1155;

            if (ex.NativeErrorCode is not (SE_ERR_NOASSOC or ERROR_NO_ASSOCIATION))
            {
                return File.Exists(path)
                    ? "Could not open the file: " + ex.Message
                    : $"File not found: {path}";
            }

            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", Quote(path)) { UseShellExecute = true });
                return null;
            }
            catch (Exception inner) { return "Could not open an editor: " + inner.Message; }
        }
        catch (Exception ex) { return "Could not open the file: " + ex.Message; }
    }

    /// <summary>Opens File Explorer with the file selected.</summary>
    public static string? RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No file to show.";

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path))
                { UseShellExecute = true });
                return null;
            }

            // The file may have rotated away; showing its folder still helps.
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", Quote(dir)) { UseShellExecute = true });
                return null;
            }

            return $"File not found: {path}";
        }
        catch (Exception ex) { return "Could not open File Explorer: " + ex.Message; }
    }

    private static string Quote(string s) => "\"" + s + "\"";
}
