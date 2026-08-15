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
    }

    public static readonly RuleSet Empty = new([], []);

    public HighlightRule? Match(string line)
    {
        for (int i = 0; i < _rules.Count; i++)
            if (_rules[i].Matches(line)) return _rules[i];
        return null;
    }
}
