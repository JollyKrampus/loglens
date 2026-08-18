using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>
/// Loads and saves the workspace JSON. Portable-first: the file sits next to the exe
/// so the whole thing travels on a USB stick. If that folder isn't writable (Program
/// Files, a read-only share) we transparently fall back to %APPDATA%.
/// </summary>
public static class WorkspaceStore
{
    public const string FileName = "loglens.workspace.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string AppDirectory
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(exe) ? AppContext.BaseDirectory : Path.GetDirectoryName(exe);
            return dir ?? AppContext.BaseDirectory;
        }
    }

    public static string RoamingDirectory
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LogLens");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Where the default workspace lives, preferring the portable location.</summary>
    public static string DefaultPath
    {
        get
        {
            var portable = Path.Combine(AppDirectory, FileName);
            if (File.Exists(portable)) return portable;

            var roaming = Path.Combine(RoamingDirectory, FileName);
            if (File.Exists(roaming)) return roaming;

            return IsWritable(AppDirectory) ? portable : roaming;
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".loglens-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static Workspace Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Workspace.CreateDefault();
            var json = File.ReadAllText(path);
            var ws = JsonSerializer.Deserialize<Workspace>(json, Options);
            if (ws is null) return Workspace.CreateDefault();

            ws.Settings ??= new AppSettings();
            ws.Rules ??= HighlightRule.Defaults();

            // Workspaces saved before 1.5.1 carry the old keyword-only defaults, which
            // let a message mentioning "Fatal" outrank the line's real |Error| level
            // field. If the rules are still EXACTLY those defaults — untouched in every
            // field — swap in the current two-tier defaults. Any edit, reorder, recolour
            // or disable means the user owns the list and it is left alone. The version
            // gate makes this one-time: without it, a 1.5.1 user deleting the six
            // "(level field)" rules would leave a list content-identical to the old
            // defaults and get them resurrected on every load.
            if (ws.Version < Workspace.CurrentVersion && IsUntouchedLegacyDefaults(ws.Rules))
                ws.Rules = HighlightRule.Defaults();
            ws.Version = Workspace.CurrentVersion;

            ws.Views ??= [];
            if (ws.Views.Count == 0) ws.Views.Add(new ViewDef { Name = "Default" });
            foreach (var v in ws.Views)
            {
                v.Sources ??= [];
                v.Rules ??= [];
            }
            return ws;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not read workspace '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The default rule set exactly as every release from 1.0 to 1.5.0 created it.
    /// Order-sensitive and compared on every field (colours normalised for a possible
    /// alpha channel), so only a genuinely untouched set matches.
    /// </summary>
    private static readonly (string Name, string Pattern, Severity Severity, string Fg, string Bg, bool Bold)[]
        LegacyDefaults =
        [
            ("Fatal",   @"\b(FATAL|CRITICAL|PANIC)\b",       Severity.Fatal, "#FFFFFF", "#8B1A1A", true),
            ("Error",   @"\b(ERROR|ERR|SEVERE|EXCEPTION)\b", Severity.Error, "#FF8A8A", "#3A1414", false),
            ("Warning", @"\b(WARN|WARNING)\b",               Severity.Warn,  "#FFC978", "#332616", false),
            ("Info",    @"\b(INFO|INFORMATION)\b",           Severity.Info,  "#8FD3FF", "",        false),
            ("Debug",   @"\b(DEBUG|DBG)\b",                  Severity.Debug, "#9E9E9E", "",        false),
            ("Trace",   @"\b(TRACE|VERBOSE)\b",              Severity.Trace, "#6E6E6E", "",        false),
            ("Stack frame", @"^\s+at\s",                     Severity.None,  "#C58A8A", "",        false),
        ];

    private static bool IsUntouchedLegacyDefaults(List<HighlightRule> rules)
    {
        if (rules.Count != LegacyDefaults.Length) return false;

        for (int i = 0; i < rules.Count; i++)
        {
            var (name, pattern, severity, fg, bg, bold) = LegacyDefaults[i];
            var r = rules[i];

            if (r.Name != name || r.Pattern != pattern || r.Severity != severity
                || !r.IsRegex || r.CaseSensitive || !r.Enabled || r.Bold != bold
                || !HexEquals(r.Foreground, fg) || !HexEquals(r.Background, bg))
                return false;
        }

        return true;
    }

    /// <summary>#FFCC0000 (as an older serialiser may have written it) equals #CC0000.</summary>
    private static bool HexEquals(string? a, string? b)
    {
        static string Norm(string? h)
        {
            h = (h ?? "").Trim().TrimStart('#').ToUpperInvariant();
            if (h.Length == 8 && h.StartsWith("FF")) h = h[2..];
            return h;
        }
        return Norm(a) == Norm(b);
    }

    public static void Save(Workspace ws, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-replace so a crash mid-save can't leave a truncated workspace.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ws, Options));

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
