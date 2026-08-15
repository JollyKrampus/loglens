using System.Collections.ObjectModel;
using System.Windows.Media;
using LogLens.Core;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.ViewModels;

/// <summary>A log group — "Dev", "Test", "Prod" — and the tabs inside it.</summary>
public sealed class ViewVm : ObservableObject, IDisposable
{
    /// <summary>Distinct, readable-on-dark colours for the merged view's source column.</summary>
    private static readonly Brush[] SourcePalette = CreatePalette(
        "#7FC8FF", "#9CE39C", "#FFC061", "#E39CE3", "#8FE3E3", "#FFA0A0", "#C6C68F", "#B0B0FF");

    private static Brush[] CreatePalette(params string[] hexes)
    {
        var brushes = new Brush[hexes.Length];
        for (int i = 0; i < hexes.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexes[i])!);
            b.Freeze();
            brushes[i] = b;
        }
        return brushes;
    }

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

    public ViewVm(ViewDef def, AppSettings settings, Func<IEnumerable<HighlightRule>> globalRules)
    {
        Def = def;
        _settings = settings;
        _globalRules = globalRules;

        def.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewDef.Name): Raise(nameof(Name)); break;
                case nameof(ViewDef.Accent): Raise(nameof(AccentBrush)); break;
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

    public Brush AccentBrush
    {
        get
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Def.Accent)!);
                b.Freeze();
                return b;
            }
            catch { return Brushes.Gray; }
        }
    }

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

    public void RefreshBadges()
    {
        Raise(nameof(ErrorTotal));
        Raise(nameof(WarnTotal));
        Raise(nameof(AlertTotal));
        Raise(nameof(HasAlert));
        Raise(nameof(HasProblem));
    }

    // ---- merged timeline ---------------------------------------------------------

    public MergedTab? Merged => _merged;

    private void SyncMergedTab()
    {
        if (Def.ShowMergedTimeline)
        {
            if (_merged is null)
            {
                _merged = new MergedTab(_settings, DescribeSources)
                {
                    ReloadAllRequested = () => { foreach (var t in FileTabs) t.ReloadFromDisk(); }
                };
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
        ReindexSources();
        _merged?.Attach(FileTabs);
        _merged?.Reseed();   // re-index invalidated the stamped source of merged lines
        SelectedTab = tab;
        return tab;
    }

    private LogTab AddTabFor(LogSource source)
    {
        var tab = new LogTab(source, _settings);

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LogPaneVm.ErrorCount) or nameof(LogPaneVm.FatalCount)
                or nameof(LogPaneVm.WarnCount) or nameof(LogPaneVm.AlertCount) or nameof(LogPaneVm.Status))
                RefreshBadges();
        };

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

    /// <summary>Keeps source indexes and colours contiguous after any add or remove.</summary>
    private void ReindexSources()
    {
        int i = 0;
        foreach (var t in FileTabs)
        {
            t.SourceIndex = i;
            t.SourceBrush = SourcePalette[i % SourcePalette.Length];
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

    public RuleSet BuildRules() => new(Def.Rules, _globalRules());

    public void StartAll()
    {
        var rules = BuildRules();
        ReindexSources();
        foreach (var t in FileTabs) t.Start(rules);
        _merged?.ApplyRules(rules);
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
