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

    /// <summary>
    /// Inside a macOS .app bundle "beside the exe" is Contents/MacOS — writing the
    /// workspace there hides it from the user and would invalidate the bundle's
    /// signature the day releases are signed. A bundle is not a portable install.
    /// </summary>
    private static bool IsInsideMacBundle =>
        AppDirectory.Replace('\\', '/').Contains(".app/Contents/MacOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the default workspace lives, preferring the portable location.</summary>
    public static string DefaultPath
    {
        get
        {
            if (!IsInsideMacBundle)
            {
                var portable = Path.Combine(AppDirectory, FileName);
                if (File.Exists(portable)) return portable;
            }

            var roaming = Path.Combine(RoamingDirectory, FileName);
            if (File.Exists(roaming)) return roaming;

            return !IsInsideMacBundle && IsWritable(AppDirectory)
                ? Path.Combine(AppDirectory, FileName)
                : roaming;
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

            // Older releases shipped weaker default rules (keyword-only before 1.5.1,
            // no exception-continuation tier in 1.5.1). If the workspace's rules are
            // still EXACTLY one of those older default sets — untouched in every
            // field — swap in the current defaults. Any edit, reorder, recolour or
            // disable means the user owns the list and it is left alone. The version
            // gate makes the upgrade one-time: without it, deleting some of the newer
            // default rules could leave a list content-identical to an older default
            // set and get the deleted rules resurrected on every load.
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
    /// Every default rule set an older release created, exactly as it created it.
    /// Order-sensitive and compared on every field (colours normalised for a possible
    /// alpha channel), so only a genuinely untouched set matches. When the defaults
    /// change shape again: bump Workspace.CurrentVersion and append the outgoing
    /// shape here.
    /// </summary>
    private static readonly (string Name, string Pattern, Severity Severity, string Fg, string Bg, bool Bold)[][]
        LegacyDefaultSets =
        [
            // 1.0 – 1.5.0: keyword-only.
            [
                ("Fatal",   @"\b(FATAL|CRITICAL|PANIC)\b",       Severity.Fatal, "#FFFFFF", "#8B1A1A", true),
                ("Error",   @"\b(ERROR|ERR|SEVERE|EXCEPTION)\b", Severity.Error, "#FF8A8A", "#3A1414", false),
                ("Warning", @"\b(WARN|WARNING)\b",               Severity.Warn,  "#FFC978", "#332616", false),
                ("Info",    @"\b(INFO|INFORMATION)\b",           Severity.Info,  "#8FD3FF", "",        false),
                ("Debug",   @"\b(DEBUG|DBG)\b",                  Severity.Debug, "#9E9E9E", "",        false),
                ("Trace",   @"\b(TRACE|VERBOSE)\b",              Severity.Trace, "#6E6E6E", "",        false),
                ("Stack frame", @"^\s+at\s",                     Severity.None,  "#C58A8A", "",        false),
            ],
            // 1.5.1: two tiers, narrower field vocabulary, no continuation rules.
            [
                ("Fatal (level field)",   @"(^|\|)\s*(FATAL|CRITICAL)\s*\|", Severity.Fatal, "#FFFFFF", "#8B1A1A", true),
                ("Error (level field)",   @"(^|\|)\s*(ERROR|SEVERE)\s*\|",   Severity.Error, "#FF8A8A", "#3A1414", false),
                ("Warning (level field)", @"(^|\|)\s*(WARN|WARNING)\s*\|",   Severity.Warn,  "#FFC978", "#332616", false),
                ("Info (level field)",    @"(^|\|)\s*(INFO)\s*\|",           Severity.Info,  "#8FD3FF", "",        false),
                ("Debug (level field)",   @"(^|\|)\s*(DEBUG|DBG)\s*\|",      Severity.Debug, "#9E9E9E", "",        false),
                ("Trace (level field)",   @"(^|\|)\s*(TRACE|VERBOSE)\s*\|",  Severity.Trace, "#6E6E6E", "",        false),
                ("Fatal",   @"\b(FATAL|CRITICAL|PANIC)\b",       Severity.Fatal, "#FFFFFF", "#8B1A1A", true),
                ("Error",   @"\b(ERROR|ERR|SEVERE|EXCEPTION)\b", Severity.Error, "#FF8A8A", "#3A1414", false),
                ("Warning", @"\b(WARN|WARNING)\b",               Severity.Warn,  "#FFC978", "#332616", false),
                ("Info",    @"\b(INFO|INFORMATION)\b",           Severity.Info,  "#8FD3FF", "",        false),
                ("Debug",   @"\b(DEBUG|DBG)\b",                  Severity.Debug, "#9E9E9E", "",        false),
                ("Trace",   @"\b(TRACE|VERBOSE)\b",              Severity.Trace, "#6E6E6E", "",        false),
                ("Stack frame", @"^\s+at\s",                     Severity.None,  "#C58A8A", "",        false),
            ],
        ];

    private static bool IsUntouchedLegacyDefaults(List<HighlightRule> rules)
    {
        foreach (var set in LegacyDefaultSets)
        {
            if (rules.Count != set.Length) continue;

            bool all = true;
            for (int i = 0; i < rules.Count && all; i++)
            {
                var (name, pattern, severity, fg, bg, bold) = set[i];
                var r = rules[i];

                all = r.Name == name && r.Pattern == pattern && r.Severity == severity
                      && r.IsRegex && !r.CaseSensitive && r.Enabled && r.Bold == bold
                      && HexEquals(r.Foreground, fg) && HexEquals(r.Background, bg);
            }

            if (all) return true;
        }

        return false;
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
