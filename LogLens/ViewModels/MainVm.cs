using System.Collections.ObjectModel;
using System.IO;
using LogLens.Core;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.ViewModels;

public sealed class MainVm : ObservableObject, IDisposable
{
    private Workspace _ws;
    private ViewVm? _selectedView;
    private string _workspacePath;
    private bool _dirty;

    public ObservableCollection<ViewVm> Views { get; } = [];

    public MainVm(Workspace ws, string path)
    {
        _ws = ws;
        _workspacePath = path;
        LoadFromWorkspace();
    }

    public AppSettings Settings => _ws.Settings;
    public AlertSettings Alerts => _ws.Alerts;
    public List<HighlightRule> GlobalRules => _ws.Rules;
    public Workspace Workspace => _ws;

    /// <summary>Bubbled up from any view so the shell can notify once, in one place.</summary>
    public event Action<ViewVm, LogTab, IReadOnlyList<LogLine>>? AlertsDetected;

    /// <summary>Every ingested batch from every view, for the issue recorder.</summary>
    public event Action<ViewVm, LogTab, IReadOnlyList<LogLine>>? LinesIngested;

    public string WorkspacePath
    {
        get => _workspacePath;
        private set { if (Set(ref _workspacePath, value)) Raise(nameof(Title)); }
    }

    public bool Dirty
    {
        get => _dirty;
        set { if (Set(ref _dirty, value)) Raise(nameof(Title)); }
    }

    public string Title =>
        $"LogLens — {Path.GetFileNameWithoutExtension(WorkspacePath)}{(Dirty ? " *" : "")}";

    public ViewVm? SelectedView
    {
        get => _selectedView;
        set
        {
            var old = _selectedView;
            if (!Set(ref _selectedView, value)) return;
            if (old is not null) old.IsActive = false;
            if (value is not null)
            {
                value.IsActive = true;
                _ws.ActiveViewId = value.Id;
            }
            Raise(nameof(HasView));
        }
    }

    public bool HasView => _selectedView is not null;

    private void LoadFromWorkspace()
    {
        foreach (var v in Views) v.Dispose();
        Views.Clear();

        foreach (var def in _ws.Views)
            Views.Add(Track(new ViewVm(def, _ws.Settings, () => _ws.Rules)));

        SelectedView = Views.FirstOrDefault(v => v.Id == _ws.ActiveViewId) ?? Views.FirstOrDefault();

        foreach (var v in Views) v.StartAll();
    }

    /// <summary>Single place where a view's alerts get forwarded to the shell.</summary>
    private ViewVm Track(ViewVm view)
    {
        view.AlertsDetected += (v, tab, lines) => AlertsDetected?.Invoke(v, tab, lines);
        view.LinesIngested += (v, tab, lines) => LinesIngested?.Invoke(v, tab, lines);
        return view;
    }

    // ---- views ------------------------------------------------------------------

    public ViewVm AddView(string name)
    {
        var def = new ViewDef { Name = name };
        _ws.Views.Add(def);
        var vm = Track(new ViewVm(def, _ws.Settings, () => _ws.Rules));
        Views.Add(vm);
        SelectedView = vm;
        Dirty = true;
        return vm;
    }

    public void DuplicateView(ViewVm source)
    {
        var def = source.Def.Clone();
        _ws.Views.Add(def);
        var vm = Track(new ViewVm(def, _ws.Settings, () => _ws.Rules));
        Views.Add(vm);
        vm.StartAll();
        SelectedView = vm;
        Dirty = true;
    }

    public void RemoveView(ViewVm view)
    {
        _ws.Views.Remove(view.Def);
        Views.Remove(view);
        view.Dispose();
        SelectedView = Views.FirstOrDefault();
        Dirty = true;
    }

    public void MoveView(ViewVm view, int delta)
    {
        int i = Views.IndexOf(view);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Views.Count) return;
        Views.Move(i, j);
        _ws.Views.RemoveAt(i);
        _ws.Views.Insert(j, view.Def);
        Dirty = true;
    }

    public void ReapplyRulesEverywhere()
    {
        foreach (var v in Views) v.ReapplyRules();
        Dirty = true;
    }

    // ---- persistence ------------------------------------------------------------

    public void Save() => SaveAs(WorkspacePath);

    public void SaveAs(string path)
    {
        WorkspaceStore.Save(_ws, path);
        WorkspacePath = path;
        Dirty = false;
    }

    public void Open(string path)
    {
        var ws = WorkspaceStore.Load(path);
        foreach (var v in Views) v.Dispose();
        Views.Clear();
        _ws = ws;
        WorkspacePath = path;
        LoadFromWorkspace();
        Dirty = false;
        Raise(nameof(Settings));
        Raise(nameof(Workspace));
    }

    public void NewWorkspace()
    {
        foreach (var v in Views) v.Dispose();
        Views.Clear();
        _ws = Workspace.CreateDefault();
        LoadFromWorkspace();
        Dirty = true;
        Raise(nameof(Settings));
        Raise(nameof(Workspace));
    }

    public void Dispose()
    {
        foreach (var v in Views) v.Dispose();
        Views.Clear();
    }
}
