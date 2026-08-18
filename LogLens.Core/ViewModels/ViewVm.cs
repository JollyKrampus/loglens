using System.Collections.ObjectModel;
using LogLens.Core;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.ViewModels;

/// <summary>A log group — "Dev", "Test", "Prod" — and the tabs inside it.</summary>
public sealed class ViewVm : ObservableObject, IDisposable
{
    /// <summary>
    /// Distinct, readable-on-dark colours for the merged view's source column.
    /// Hex, not brushes: LogLine is cross-platform and the view converts.
    /// </summary>
    private static readonly string[] SourcePalette =
        ["#7FC8FF", "#9CE39C", "#FFC061", "#E39CE3", "#8FE3E3", "#FFA0A0", "#C6C68F", "#B0B0FF"];

    private readonly AppSettings _settings;
    private readonly Func<IEnumerable<HighlightRule>> _globalRules;
    private LogPaneVm? _selectedTab;
    private bool _isActive;
    private MergedTab? _merged;

    public ViewDef Def { get; }

    /// <summary>Every pane in this view: the merged timeline first, if enabled, then the files.</summary>
    public ObservableCollection<LogPaneVm> Tabs { get; } = [];

    /// <summary>Raised when a file tab ingests error-level lines, for the alert service.</summary>
    public event Action<ViewVm, LogTab, IReadOnlyList<LogLine>>? AlertsDetected;

    /// <summary>Raised for every ingested batch, for the issue recorder.</summary>
    public event Action<ViewVm, LogTab, IReadOnlyList<LogLine>>? LinesIngested;

    /// <summary>Chips, filters or follow changed on some pane — the workspace is dirty.</summary>
    public event Action? StateChanged;

    /// <summary>A tab noticed its file is pipe-delimited while the rules are keyword-loose.</summary>
    public event Action<string>? FormatHint;

    /// <summary>The shell shows this to the user however it likes (message box, toast…).</summary>
    public Action<string>? NotifyUser;

    private readonly IUiThread _ui;

    public ViewVm(ViewDef def, AppSettings settings, Func<IEnumerable<HighlightRule>> globalRules, IUiThread ui)
    {
        Def = def;
        _settings = settings;
        _globalRules = globalRules;
        _ui = ui;

        def.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewDef.Name): Raise(nameof(Name)); break;
                case nameof(ViewDef.ShowMergedTimeline): SyncMergedTab(); break;
            }
        };

        foreach (var s in def.Sources) AddTabFor(s);
        SyncMergedTab();
        SelectedTab = Tabs.FirstOrDefault();
    }

    public string Name => Def.Name;
    public string Id => Def.Id;

    public IEnumerable<LogTab> FileTabs => Tabs.OfType<LogTab>();


    public LogPaneVm? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var old = _selectedTab;
            if (!Set(ref _selectedTab, value)) return;
            if (old is not null) old.IsActive = false;
            if (value is not null && _isActive) value.IsActive = true;
        }
    }

    /// <summary>True while this view is the one on screen.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            if (SelectedTab is not null) SelectedTab.IsActive = value;
        }
    }

    // ---- aggregate badge for the sidebar ----------------------------------------
    // Deliberately over FileTabs only: the merged tab holds copies of the same lines,
    // so including it would double every count in the sidebar.

    public int ErrorTotal => FileTabs.Sum(t => t.ErrorCount + t.FatalCount);
    public int WarnTotal => FileTabs.Sum(t => t.WarnCount);
    public int AlertTotal => FileTabs.Sum(t => t.AlertCount);
    public bool HasAlert => AlertTotal > 0;
    public bool HasProblem => FileTabs.Any(t => t.HasError);

    /// <summary>Bool form of the badge for bindings without a positive-int converter.</summary>
    public bool HasProblemOrErrors => ErrorTotal > 0;

    public void RefreshBadges()
    {
        Raise(nameof(ErrorTotal));
        Raise(nameof(WarnTotal));
        Raise(nameof(AlertTotal));
        Raise(nameof(HasAlert));
        Raise(nameof(HasProblem));
        Raise(nameof(HasProblemOrErrors));
    }

    // ---- merged timeline ---------------------------------------------------------

    public MergedTab? Merged => _merged;

    private void SyncMergedTab()
    {
        if (Def.ShowMergedTimeline)
        {
            if (_merged is null)
            {
                _merged = new MergedTab(_settings, DescribeSources, _ui)
                {
                    ReloadAllRequested = () => { foreach (var t in FileTabs) t.ReloadFromDisk(); },

                    // "Go to this line's file tab": switch tabs, then reveal the line
                    // once the pane has rebound to the new tab — hence the Post.
                    // A miss is reported through the shell, never silent: a tab switch
                    // with nothing selected reads as the feature not working.
                    NavigateToSourceRequested = (tab, line) =>
                    {
                        SelectedTab = tab;
                        _ui.Post(() =>
                        {
                            switch (tab.RevealLineByNumber(line.SourceLineNumber))
                            {
                                case LogPaneVm.RevealOutcome.HiddenByFilters:
                                    NotifyUser?.Invoke(
                                        "That line is in this tab, but currently hidden by its filters — "
                                        + "clear the Show/Hide filters or severity chips to see it.");
                                    break;

                                case LogPaneVm.RevealOutcome.NotInBuffer:
                                    NotifyUser?.Invoke("That line has scrolled out of this tab's buffer.");
                                    break;
                            }
                        });
                    }
                };
                SubscribeStateChanges(_merged);
                Tabs.Insert(0, _merged);
                _merged.ApplyRules(BuildRules());
            }

            _merged.Attach(FileTabs);

            // Seed from what the tabs already hold. Reloading them from disk would
            // work too, but it clears every file tab's buffer and scroll position —
            // turning the merged view on must not destroy the panes beside it.
            _merged.Reseed();
        }
        else if (_merged is not null)
        {
            if (ReferenceEquals(SelectedTab, _merged)) SelectedTab = FileTabs.FirstOrDefault();
            Tabs.Remove(_merged);
            _merged.Dispose();
            _merged = null;
        }

        Raise(nameof(Merged));
    }

    private string DescribeSources()
    {
        var names = FileTabs.Select(t => t.Header).ToList();
        return names.Count == 0 ? "no files" : string.Join("  +  ", names);
    }

    // ---- tabs -------------------------------------------------------------------

    public LogTab AddSource(LogSource source)
    {
        Def.Sources.Add(source);
        var tab = AddTabFor(source);
        tab.Start(BuildRules());
        tab.ApplyPaneState(source.Pane);
        ReindexSources();
        _merged?.Attach(FileTabs);
        _merged?.Reseed();   // re-index invalidated the stamped source of merged lines
        SelectedTab = tab;
        return tab;
    }

    private LogTab AddTabFor(LogSource source)
    {
        var tab = new LogTab(source, _settings, _ui);

        tab.FormatHintDetected += m => FormatHint?.Invoke(m);

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LogPaneVm.ErrorCount) or nameof(LogPaneVm.FatalCount)
                or nameof(LogPaneVm.WarnCount) or nameof(LogPaneVm.AlertCount) or nameof(LogPaneVm.Status))
                RefreshBadges();
        };
        SubscribeStateChanges(tab);

        tab.AlertsDetected += (t, lines) => AlertsDetected?.Invoke(this, t, lines);

        tab.LinesAppended += e =>
        {
            // A rewind carries no lines; it only signals that the file restarted.
            if (!e.Rewound) LinesIngested?.Invoke(this, e.Tab, e.Lines);
        };

        Tabs.Add(tab);
        ReindexSources();
        return tab;
    }

    /// <summary>
    /// Chips, filters and follow are workspace state now — any change marks the
    /// workspace dirty so auto-save picks it up.
    /// </summary>
    private void SubscribeStateChanges(LogPaneVm pane)
    {
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LogPaneVm.ShowFatal) or nameof(LogPaneVm.ShowError)
                or nameof(LogPaneVm.ShowWarn) or nameof(LogPaneVm.ShowInfo)
                or nameof(LogPaneVm.ShowDebug) or nameof(LogPaneVm.FollowTail))
                StateChanged?.Invoke();
        };
        pane.Filter.PropertyChanged += (_, __) => StateChanged?.Invoke();
    }

    /// <summary>Keeps source indexes and colours contiguous after any add or remove.</summary>
    private void ReindexSources()
    {
        int i = 0;
        foreach (var t in FileTabs)
        {
            t.SourceIndex = i;
            t.SourceColor = SourcePalette[i % SourcePalette.Length];
            i++;
        }
    }

    public void RemoveTab(LogPaneVm tab)
    {
        if (tab is not LogTab file) return;   // the merged tab is removed via its setting

        Def.Sources.Remove(file.Source);
        Tabs.Remove(file);
        file.Dispose();

        ReindexSources();
        _merged?.Attach(FileTabs);
        _merged?.Reseed();   // re-index invalidated the stamped source of merged lines

        SelectedTab = Tabs.FirstOrDefault();
        RefreshBadges();
    }

    /// <summary>Writes every pane's live chips/filters into the model before a save.</summary>
    public void CapturePaneStates()
    {
        foreach (var t in FileTabs) t.CapturePaneState(t.Source.Pane);
        _merged?.CapturePaneState(Def.MergedPane);
    }

    public RuleSet BuildRules() => new(Def.Rules, _globalRules());

    public void StartAll()
    {
        var rules = BuildRules();
        ReindexSources();
        foreach (var t in FileTabs)
        {
            t.Start(rules);
            t.ApplyPaneState(t.Source.Pane);
        }
        _merged?.ApplyRules(rules);
        _merged?.ApplyPaneState(Def.MergedPane);
        _merged?.Attach(FileTabs);
    }

    public void ReapplyRules()
    {
        var rules = BuildRules();
        foreach (var t in Tabs) t.ApplyRules(rules);
        RefreshBadges();
    }

    public void Dispose()
    {
        foreach (var t in Tabs) t.Dispose();
        Tabs.Clear();
        _merged = null;
    }
}
