using System.Text.RegularExpressions;
using LogLens.Models;

namespace LogLens.Services;

/// <summary>
/// Find-in-tab, minus the find bar: which displayed rows match, and which one
/// "next" and "previous" land on.
///
/// This was code-behind in the WPF LogPane. It moved here when the macOS app
/// gained find, because the parts that matter — plain versus regex, case, wrap
/// at either end, stepping from wherever the selection is rather than from the
/// last hit — are exactly the parts two hand-copied versions would disagree on.
/// Each shell keeps only its find bar and its ListBox.
///
/// Hits are cached against a key that includes the displayed line count, so
/// stepping repeatedly is cheap but a filter change or new lines arriving
/// re-scan on the next step instead of jumping to a stale index.
/// </summary>
public sealed class LineFinder
{
    private List<int> _hits = [];
    private string _key = "";

    /// <summary>Indexes into the pane's Display list, ascending.</summary>
    public IReadOnlyList<int> Hits => _hits;

    /// <summary>"12 matches", "3 of 12", "no matches", a regex error, or empty.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Forget everything — the pane changed under the find bar.</summary>
    public void Reset()
    {
        _hits = [];
        _key = "";
        Status = "";
    }

    private static string KeyFor(string? term, bool isRegex, bool matchCase, int shownLines)
        => $"{term}\0{isRegex}\0{matchCase}\0{shownLines}";

    /// <summary>Re-scans <paramref name="items"/>. Sets <see cref="Status"/>.</summary>
    public void Search(IReadOnlyList<LogLine>? items, string? term, bool isRegex, bool matchCase)
    {
        _hits = [];
        _key = KeyFor(term, isRegex, matchCase, items?.Count ?? -1);

        if (items is null || string.IsNullOrEmpty(term))
        {
            Status = "";
            return;
        }

        Regex? rx = null;
        if (isRegex)
        {
            try
            {
                var opts = RegexOptions.CultureInvariant;
                if (!matchCase) opts |= RegexOptions.IgnoreCase;
                rx = new Regex(term, opts, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                Status = ex.Message;
                return;
            }
        }

        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            bool hit;
            if (rx is null) hit = text.Contains(term, cmp);
            else
            {
                // Same rule as highlight rules: a pathological pattern times out
                // into "no match" on that line rather than throwing out of a click.
                try { hit = rx.IsMatch(text); }
                catch (RegexMatchTimeoutException) { hit = false; }
            }
            if (hit) _hits.Add(i);
        }

        Status = _hits.Count == 0 ? "no matches" : $"{_hits.Count:N0} matches";
    }

    /// <summary>
    /// The row to select for next (<paramref name="direction"/> &gt; 0) or previous,
    /// starting from <paramref name="currentIndex"/> (the selection, or -1), wrapping
    /// at either end. Re-scans first if anything that changes the answer changed.
    /// Returns -1 when nothing matches.
    /// </summary>
    public int Step(IReadOnlyList<LogLine>? items, string? term, bool isRegex, bool matchCase,
                    int currentIndex, int direction)
    {
        if (items is null) return -1;
        if (_key != KeyFor(term, isRegex, matchCase, items.Count))
            Search(items, term, isRegex, matchCase);
        if (_hits.Count == 0) return -1;

        int target = -1;
        if (direction > 0)
        {
            foreach (var i in _hits) if (i > currentIndex) { target = i; break; }
            if (target < 0) target = _hits[0];               // wrap to the top
        }
        else
        {
            for (int k = _hits.Count - 1; k >= 0; k--)
                if (_hits[k] < currentIndex) { target = _hits[k]; break; }
            if (target < 0) target = _hits[^1];              // wrap to the bottom
        }

        int ordinal = _hits.BinarySearch(target) + 1;
        Status = $"{ordinal:N0} of {_hits.Count:N0}";
        return target;
    }
}
