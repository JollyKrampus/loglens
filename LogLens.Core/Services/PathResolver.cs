using System.IO;

namespace LogLens.Services;

/// <summary>
/// Turns a path spec into a concrete file. If the spec contains a wildcard we pick
/// the most recently written match, which is what you want for rolling logs such as
/// app-20260814.log — the tab follows the roll automatically.
/// </summary>
public static class PathResolver
{
    public static bool IsWildcard(string spec)
        => spec.Contains('*') || spec.Contains('?');

    public static string? Resolve(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        try
        {
            if (!IsWildcard(spec))
                return File.Exists(spec) ? spec : null;

            var dir = Path.GetDirectoryName(spec);
            var pattern = Path.GetFileName(spec);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pattern)) return null;
            if (!Directory.Exists(dir)) return null;

            FileInfo? newest = null;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                if (newest is null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
            }
            return newest?.FullName;
        }
        catch { return null; }
    }
}
