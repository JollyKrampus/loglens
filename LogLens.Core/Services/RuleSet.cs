using System.Text.RegularExpressions;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>
/// The rules in effect for one tab: the view's own rules first, then the workspace-wide
/// ones. First match wins, so a view-specific rule can override a global one.
/// </summary>
public sealed class RuleSet
{
    private readonly List<HighlightRule> _rules;

    public RuleSet(IEnumerable<HighlightRule> viewRules, IEnumerable<HighlightRule> globalRules)
    {
        _rules = viewRules.Concat(globalRules).Where(r => r.Enabled).ToList();
        HasLooseSeverityRules = _rules.Any(r =>
            r.Severity != Severity.None && (!r.IsRegex || !r.Pattern.Contains(@"(^|\|)")));
    }

    public static readonly RuleSet Empty = new([], []);

    /// <summary>
    /// True when any severity-carrying rule matches loose keywords rather than an
    /// anchored pipe field — i.e. a message merely mentioning "error" can still set
    /// the line's severity. Drives the "this looks like NLog" hint.
    /// </summary>
    public bool HasLooseSeverityRules { get; }

    public HighlightRule? Match(string line)
    {
        for (int i = 0; i < _rules.Count; i++)
            if (_rules[i].Matches(line)) return _rules[i];
        return null;
    }

    private static readonly Regex PipeLevel = new(
        @"(^|\|)\s*(FATAL|CRITICAL|ERROR|ERR|SEVERE|WARN|WARNING|INFO|INFORMATION|DEBUG|DBG|TRACE|VERBOSE)\s*\|",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when a sample of lines is clearly the pipe-delimited NLog/log4net shape:
    /// enough lines carry a level in its own pipe field. Continuation lines (stack
    /// traces) legitimately carry none, hence the 40% bar rather than a majority.
    /// </summary>
    public static bool LooksPipeLevelled(IReadOnlyList<string> lines)
    {
        int sampled = 0, matched = 0;
        for (int i = 0; i < lines.Count && sampled < 200; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            sampled++;
            if (PipeLevel.IsMatch(lines[i])) matched++;
        }

        return sampled >= 5 && matched * 10 >= sampled * 4;
    }
}
