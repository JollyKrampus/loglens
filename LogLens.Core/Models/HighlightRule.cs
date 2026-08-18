using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LogLens.Core;

namespace LogLens.Models;

public enum Severity { None, Trace, Debug, Info, Warn, Error, Fatal }

/// <summary>
/// One highlight rule. Rules are evaluated top-down and the first match wins,
/// which is how BareTail behaves — order matters, so put FATAL above ERROR.
/// </summary>
public sealed class HighlightRule : ObservableObject
{
    private string _name = "New rule";
    private string _pattern = "";
    private bool _isRegex = true;
    private bool _caseSensitive;
    private bool _enabled = true;
    private bool _bold;
    private string _foreground = "#E6E6E6";
    private string _background = "";
    private Severity _severity = Severity.None;

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Pattern
    {
        get => _pattern;
        set { if (Set(ref _pattern, value)) Invalidate(); }
    }

    public bool IsRegex
    {
        get => _isRegex;
        set { if (Set(ref _isRegex, value)) Invalidate(); }
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set { if (Set(ref _caseSensitive, value)) Invalidate(); }
    }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool Bold { get => _bold; set => Set(ref _bold, value); }

    /// <summary>
    /// Hex colour, e.g. #FFCCCC. Empty means "inherit the theme default". Kept as a
    /// string on purpose — this type is cross-platform and the view layer owns the
    /// conversion to whatever brush type its framework uses.
    /// </summary>
    public string Foreground
    {
        get => _foreground;
        set => Set(ref _foreground, value);
    }

    public string Background
    {
        get => _background;
        set => Set(ref _background, value);
    }

    /// <summary>Feeds the per-view counters ("3 errors in prod") and the tab alert dot.</summary>
    public Severity Severity { get => _severity; set => Set(ref _severity, value); }

    // ---- runtime, not persisted -------------------------------------------------

    [JsonIgnore] private Regex? _compiled;
    [JsonIgnore] private bool _compileFailed;

    [JsonIgnore] public string? CompileError { get; private set; }

    private void Invalidate()
    {
        _compiled = null;
        _compileFailed = false;
        CompileError = null;
        Raise(nameof(CompileError));
    }

    /// <summary>True if this rule claims the line. Bad regexes are inert, never fatal.</summary>
    public bool Matches(string line)
    {
        if (!Enabled || string.IsNullOrEmpty(Pattern)) return false;

        if (!IsRegex)
        {
            return line.Contains(Pattern,
                CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        if (_compiled is null)
        {
            if (_compileFailed) return false;
            try
            {
                var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (!CaseSensitive) opts |= RegexOptions.IgnoreCase;
                _compiled = new Regex(Pattern, opts, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                _compileFailed = true;
                CompileError = ex.Message;
                Raise(nameof(CompileError));
                return false;
            }
        }

        try { return _compiled.IsMatch(line); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public HighlightRule Clone() => new()
    {
        Name = Name, Pattern = Pattern, IsRegex = IsRegex, CaseSensitive = CaseSensitive,
        Enabled = Enabled, Bold = Bold, Foreground = Foreground, Background = Background,
        Severity = Severity
    };

    /// <summary>The out-of-the-box rule set: severity keywords, first match wins.</summary>
    public static List<HighlightRule> Defaults() =>
    [
        new() { Name = "Fatal",   Pattern = @"\b(FATAL|CRITICAL|PANIC)\b",        Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
        new() { Name = "Error",   Pattern = @"\b(ERROR|ERR|SEVERE|EXCEPTION)\b",  Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
        new() { Name = "Warning", Pattern = @"\b(WARN|WARNING)\b",                Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
        new() { Name = "Info",    Pattern = @"\b(INFO|INFORMATION)\b",            Severity = Severity.Info,  Foreground = "#8FD3FF" },
        new() { Name = "Debug",   Pattern = @"\b(DEBUG|DBG)\b",                   Severity = Severity.Debug, Foreground = "#9E9E9E" },
        new() { Name = "Trace",   Pattern = @"\b(TRACE|VERBOSE)\b",               Severity = Severity.Trace, Foreground = "#6E6E6E" },
        new() { Name = "Stack frame", Pattern = @"^\s+at\s",                      Severity = Severity.None,  Foreground = "#C58A8A" },
    ];
}
