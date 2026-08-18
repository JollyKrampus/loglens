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
