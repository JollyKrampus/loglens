using LogLens.Models;

namespace LogLens.Services;

public sealed record RulePreset(string Name, string Description, Func<List<HighlightRule>> Build);

/// <summary>
/// Ready-made rule sets for the log formats people actually have.
///
/// The generic preset (also the defaults) honours a pipe-delimited level field
/// when one exists and falls back to matching severity keywords anywhere on the
/// line. The format-specific presets anchor to the level field exclusively, so
/// only the real level column ever counts.
/// </summary>
public static class RulePresets
{
    public static IReadOnlyList<RulePreset> All =>
    [
        new("Severity keywords (generic)",
            "The default rules. A pipe-delimited level field decides when one exists; "
            + "otherwise FATAL/ERROR/WARN/INFO/DEBUG match anywhere on the line. "
            + "Works with almost any format.",
            HighlightRule.Defaults),

        new("NLog / log4net (pipe-delimited)",
            @"For layouts like ${longdate}|${level:uppercase=true}|${logger}|${message}. "
            + "Only matches the level when it is its own pipe-delimited field, so a message "
            + "mentioning 'error' will not be mis-coloured.",
            NLogPipe),

        new("NLog JsonLayout",
            @"For NLog's JsonLayout — matches ""level"": ""Error"" rather than loose keywords.",
            NLogJson),

        new("Serilog / short levels",
            "For layouts using three-letter levels — VRB, DBG, INF, WRN, ERR, FTL — "
            + "such as Serilog's default console and file sinks.",
            ShortLevels),
    ];

    /// <summary>
    /// Level as its own pipe-delimited field. The (^|\|) ... \| shape means it works
    /// whether the level is the first, second or fifth column, so it survives someone
    /// reordering the layout without needing a different preset.
    /// </summary>
    private static List<HighlightRule> NLogPipe()
    {
        static string Field(string levels) => $@"(^|\|)\s*({levels})\s*\|";

        return
        [
            new() { Name = "Fatal",   Pattern = Field("FATAL"),        Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
            new() { Name = "Error",   Pattern = Field("ERROR"),        Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
            new() { Name = "Warning", Pattern = Field("WARN|WARNING"), Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
            new() { Name = "Info",    Pattern = Field("INFO"),         Severity = Severity.Info,  Foreground = "#8FD3FF" },
            new() { Name = "Debug",   Pattern = Field("DEBUG"),        Severity = Severity.Debug, Foreground = "#9E9E9E" },
            new() { Name = "Trace",   Pattern = Field("TRACE"),        Severity = Severity.Trace, Foreground = "#6E6E6E" },

            // ${exception:format=tostring} spills onto following lines that carry no
            // level field. These colour the continuation so an exception reads as one
            // block, but count as None so they don't inflate the error totals.
            new() { Name = "Exception type", Pattern = @"^\s*\w+(\.\w+)*Exception[:,]", Severity = Severity.None, Foreground = "#FF9E9E", Bold = true },
            new() { Name = "Stack frame",    Pattern = @"^\s+at\s",                     Severity = Severity.None, Foreground = "#C58A8A" },
            new() { Name = "Inner exception", Pattern = @"--->|End of inner exception",  Severity = Severity.None, Foreground = "#C58A8A" },
        ];
    }

    /// <summary>
    /// Serilog writes levels as three letters, usually bracketed: "[12:52:40 INF]".
    /// Anchoring on the bracket or a word boundary keeps "ERR" from matching inside
    /// a word like "ERRATIC".
    /// </summary>
    private static List<HighlightRule> ShortLevels()
    {
        static string Lvl(string levels) => $@"(^|\[|\s)({levels})(\]|\s|$)";

        return
        [
            new() { Name = "Fatal",   Pattern = Lvl("FTL|FATAL"),   Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
            new() { Name = "Error",   Pattern = Lvl("ERR|ERROR"),   Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
            new() { Name = "Warning", Pattern = Lvl("WRN|WARN"),    Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
            new() { Name = "Info",    Pattern = Lvl("INF|INFO"),    Severity = Severity.Info,  Foreground = "#8FD3FF" },
            new() { Name = "Debug",   Pattern = Lvl("DBG|DEBUG"),   Severity = Severity.Debug, Foreground = "#9E9E9E" },
            new() { Name = "Verbose", Pattern = Lvl("VRB|TRACE"),   Severity = Severity.Trace, Foreground = "#6E6E6E" },
            new() { Name = "Exception type", Pattern = @"^\s*\w+(\.\w+)*Exception[:,]", Severity = Severity.None, Foreground = "#FF9E9E", Bold = true },
            new() { Name = "Stack frame",    Pattern = @"^\s+at\s",                     Severity = Severity.None, Foreground = "#C58A8A" },
        ];
    }

    private static List<HighlightRule> NLogJson()
    {
        static string Json(string level) => $@"""(level|Level|LogLevel)""\s*:\s*""{level}""";

        return
        [
            new() { Name = "Fatal",   Pattern = Json("Fatal"),   Severity = Severity.Fatal, Foreground = "#FFFFFF", Background = "#8B1A1A", Bold = true },
            new() { Name = "Error",   Pattern = Json("Error"),   Severity = Severity.Error, Foreground = "#FF8A8A", Background = "#3A1414" },
            new() { Name = "Warning", Pattern = Json("Warn"),    Severity = Severity.Warn,  Foreground = "#FFC978", Background = "#332616" },
            new() { Name = "Info",    Pattern = Json("Info"),    Severity = Severity.Info,  Foreground = "#8FD3FF" },
            new() { Name = "Debug",   Pattern = Json("Debug"),   Severity = Severity.Debug, Foreground = "#9E9E9E" },
            new() { Name = "Trace",   Pattern = Json("Trace"),   Severity = Severity.Trace, Foreground = "#6E6E6E" },
            new() { Name = "Exception field", Pattern = @"""(exception|Exception)""\s*:\s*""(?!"")", Severity = Severity.None, Foreground = "#FF9E9E" },
        ];
    }
}
