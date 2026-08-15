using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using LogLens.Core;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.ViewModels;

/// <summary>Compiled form of a <see cref="FilterSpec"/>. Bad regexes filter nothing.</summary>
internal sealed class FilterMatcher
{
    private readonly string? _include, _exclude;
    private readonly Regex? _includeRx, _excludeRx;
    private readonly StringComparison _cmp;

    public bool Active { get; }
    public string? Error { get; }

    public FilterMatcher(FilterSpec spec)
    {
        _cmp = spec.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var inc = spec.Include?.Trim() ?? "";
        var exc = spec.Exclude?.Trim() ?? "";
        Active = inc.Length > 0 || exc.Length > 0;
        if (!Active) return;

        if (spec.IsRegex)
        {
            var opts = RegexOptions.CultureInvariant;
            if (!spec.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            try
            {
                if (inc.Length > 0) _includeRx = new Regex(inc, opts, TimeSpan.FromMilliseconds(100));
                if (exc.Length > 0) _excludeRx = new Regex(exc, opts, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _includeRx = _excludeRx = null;
                Active = false;
            }
        }
        else
        {
            if (inc.Length > 0) _include = inc;
            if (exc.Length > 0) _exclude = exc;
        }
    }

    public bool Passes(string line)
    {
        if (!Active) return true;
        try
        {
            if (_includeRx is not null && !_includeRx.IsMatch(line)) return false;
            if (_include is not null && !line.Contains(_include, _cmp)) return false;
            if (_excludeRx is not null && _excludeRx.IsMatch(line)) return false;
            if (_exclude is not null && line.Contains(_exclude, _cmp)) return false;
            return true;
        }
        catch (RegexMatchTimeoutException) { return true; }
    }
}

/// <summary>
/// Everything a pane needs regardless of where its lines come from: the ring buffer,
/// the live filter, the severity counters and the follow/pause state.
///
/// <see cref="LogTab"/> fills it from a file. <see cref="MergedTab"/> fills it from
/// the other tabs in the view. The XAML binds to this type, so both render identically.
/// </summary>
public abstract class LogPaneVm : ObservableObject, IDisposable
{
    protected readonly Dispatcher Ui = Application.Current.Dispatcher;
    protected readonly AppSettings Settings;
    protected readonly List<LogLine> All = new();

    private FilterMatcher _matcher;
    protected RuleSet Rules = RuleSet.Empty;

    private bool _followTail = true;
    private bool _paused;
    private bool _isActive;
    private string? _status;
    private string? _filterError;
    private int _alertCount;

    protected LogPaneVm(AppSettings settings)
    {
        Settings = settings;
        Filter = new FilterSpec();
        _matcher = new FilterMatcher(Filter);
        Filter.PropertyChanged += (_, __) => ApplyFilter();
    }

    public FilterSpec Filter { get; }
    public BulkObservableCollection<LogLine> Display { get; } = new();

    /// <summary>Tab caption.</summary>
    public abstract string Header { get; }

    /// <summary>Shown in the status bar; a path for files, a summary for the merged view.</summary>
    public abstract string PathSpec { get; }

    /// <summary>True for the merged timeline, which shows a source column instead of line numbers.</summary>
    public virtual bool ShowsSourceColumn => false;

    public string? Status { get => _status; protected set => Set(ref _status, value); }
    public string? FilterError { get => _filterError; private set => Set(ref _filterError, value); }
    public bool HasError => !string.IsNullOrEmpty(Status);

    public bool FollowTail
    {
        get => _followTail;
        set { if (Set(ref _followTail, value) && value) RequestScrollToEnd?.Invoke(); }
    }

    public virtual bool Paused
    {
        get => _paused;
        set
        {
            if (!Set(ref _paused, value)) return;
            if (!value) FlushPending();
        }
    }

    /// <summary>Set by the shell when this tab is on screen; clears the alert badge.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            if (value) AlertCount = 0;
        }
    }

    /// <summary>Errors/fatals seen while you weren't looking at this tab.</summary>
    public int AlertCount
    {
        get => _alertCount;
        protected set { if (Set(ref _alertCount, value)) Raise(nameof(HasAlert)); }
    }

    public bool HasAlert => _alertCount > 0;

    public int TotalLines => All.Count;
    public int ShownLines => Display.Count;

    public int FatalCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int WarnCount { get; private set; }
    public int InfoCount { get; private set; }
    public int DebugCount { get; private set; }

    public string CountsSummary =>
        $"{TotalLines:N0} lines" + (_matcher.Active ? $" · {ShownLines:N0} shown" : "");

    public Action? RequestScrollToEnd;

    protected bool FilterActive => _matcher.Active;
    protected bool PassesFilter(string text) => _matcher.Passes(text);

    // ---- commands ---------------------------------------------------------------

    public abstract void ReloadFromDisk();

    /// <summary>Clears the on-screen buffer. Never touches the file.</summary>
    public virtual void ClearBuffer()
    {
        All.Clear();
        Display.ReplaceAll(Array.Empty<LogLine>());
        ResetCounters();
        RaiseStats();
    }

    public void ApplyRules(RuleSet rules)
    {
        Rules = rules;
        Rebuild();
    }

    protected virtual void FlushPending() { }

    // ---- ingest -----------------------------------------------------------------

    /// <summary>Adds already-constructed lines and updates counters, filter and scroll.</summary>
    protected void AppendLines(IReadOnlyList<LogLine> lines, out int newAlerts)
    {
        newAlerts = 0;
        if (lines.Count == 0) { RaiseStats(); return; }

        var shown = new List<LogLine>(lines.Count);

        foreach (var line in lines)
        {
            All.Add(line);
            Count(line.Severity, +1);
            if (line.Severity is Severity.Error or Severity.Fatal) newAlerts++;
            if (PassesFilter(line.Text)) shown.Add(line);
        }

        Display.AddRange(shown);
        TrimIfNeeded();
        RaiseStats();

        if (newAlerts > 0 && !IsActive) AlertCount += newAlerts;
        if (FollowTail && shown.Count > 0) RequestScrollToEnd?.Invoke();
    }

    protected void ResetBuffer()
    {
        All.Clear();
        Display.ReplaceAll(Array.Empty<LogLine>());
        ResetCounters();
    }

    protected void TrimIfNeeded()
    {
        int max = Settings.MaxLines;
        if (All.Count <= max) return;

        // Drop in chunks so we aren't rebuilding on every single new line.
        int drop = Math.Max(All.Count - max, max / 10);
        drop = Math.Min(drop, All.Count);

        All.RemoveRange(0, drop);
        RecountAll();
        Rebuild();
    }

    // ---- filtering / rebuild -----------------------------------------------------

    private void ApplyFilter()
    {
        _matcher = new FilterMatcher(Filter);
        FilterError = _matcher.Error;
        Rebuild();
    }

    protected void Rebuild()
    {
        var rebuilt = new List<LogLine>(All.Count);

        for (int i = 0; i < All.Count; i++)
        {
            var l = All[i];

            // Re-resolve the rule so edits to highlighting show up immediately.
            var rule = Rules.Match(l.Text);
            if (!ReferenceEquals(rule, l.Rule))
            {
                l = l.WithRule(rule);
                All[i] = l;
            }

            if (PassesFilter(l.Text)) rebuilt.Add(l);
        }

        RecountAll();
        Display.ReplaceAll(rebuilt);
        RaiseStats();
        if (FollowTail) RequestScrollToEnd?.Invoke();
    }

    // ---- counters ---------------------------------------------------------------

    protected void ResetCounters() => FatalCount = ErrorCount = WarnCount = InfoCount = DebugCount = 0;

    protected void RecountAll()
    {
        ResetCounters();
        foreach (var l in All) Count(l.Severity, +1);
    }

    private void Count(Severity s, int delta)
    {
        switch (s)
        {
            case Severity.Fatal: FatalCount += delta; break;
            case Severity.Error: ErrorCount += delta; break;
            case Severity.Warn:  WarnCount  += delta; break;
            case Severity.Info:  InfoCount  += delta; break;
            case Severity.Debug: DebugCount += delta; break;
        }
    }

    protected void RaiseStats()
    {
        Raise(nameof(TotalLines));
        Raise(nameof(ShownLines));
        Raise(nameof(FatalCount));
        Raise(nameof(ErrorCount));
        Raise(nameof(WarnCount));
        Raise(nameof(InfoCount));
        Raise(nameof(DebugCount));
        Raise(nameof(CountsSummary));
    }

    public virtual void Dispose() { }
}
