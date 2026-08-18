using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LogLens.Core;
using LogLens.Models;
using LogLens.Services;
using LogLens.ViewModels;

namespace LogLens.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainVm _vm;
    private readonly IssueRecorder _issues;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        UpdateService.CleanUpLeftovers();

        var path = WorkspaceStore.DefaultPath;
        Workspace ws;
        try { ws = WorkspaceStore.Load(path); }
        catch { ws = Workspace.CreateDefault(); }

        _vm = new MainVm(ws, path, new AvaloniaUiThread());
        _vm.UserMessage += Notify;
        DataContext = _vm;

        var dbPath = Path.Combine(
            Path.GetDirectoryName(_vm.WorkspacePath) ?? WorkspaceStore.RoamingDirectory,
            "loglens.issues.db");
        _issues = new IssueRecorder(new IssueStore(dbPath), _vm.Settings);
        _vm.LinesIngested += (view, tab, lines) => _issues.Observe(view.Name, tab.Header, lines);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, __) => { Toast.Text = ""; _toastTimer.Stop(); };

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoSaveTimer.Tick += (_, __) =>
        {
            if (!_vm.Dirty || !_vm.Settings.AutoSaveWorkspace) return;
            try { _vm.Save(); } catch { }
        };
        _autoSaveTimer.Start();

        Closing += (_, __) =>
        {
            try { _vm.Save(); } catch { }
            _issues.Dispose();
            _vm.Dispose();
        };
    }

    private ViewVm? View => _vm.SelectedView;
    private LogPaneVm? Pane => _vm.SelectedView?.SelectedTab;

    private void Notify(string message)
    {
        Toast.Text = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---- log pane wiring ---------------------------------------------------------

    /// <summary>
    /// The pane's ListBox is realised per selected tab; hook the view-model's scroll
    /// and reveal callbacks to whichever instance is live right now.
    /// </summary>
    private void LinesList_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ListBox list) return;

        void Wire()
        {
            if (list.DataContext is not LogPaneVm pane) return;

            pane.RequestScrollToEnd = () => Dispatcher.UIThread.Post(() =>
            {
                if (pane.FollowTail && pane.Display.Count > 0)
                    list.ScrollIntoView(pane.Display[^1]);
            }, DispatcherPriority.Background);

            pane.RevealIndexRequested = i => Dispatcher.UIThread.Post(() =>
            {
                if (i >= 0 && i < pane.Display.Count)
                {
                    list.SelectedIndex = i;
                    list.ScrollIntoView(pane.Display[i]);
                }
            });

            if (pane.FollowTail && pane.Display.Count > 0)
                list.ScrollIntoView(pane.Display[^1]);
        }

        Wire();
        list.DataContextChanged += (_, __) => Wire();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as LogPaneVm)?.ClearBuffer();

    private void Reload_Click(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as LogPaneVm)?.ReloadFromDisk();

    // ---- menu --------------------------------------------------------------------

    private async void OpenWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open workspace",
            FileTypeFilter = [new FilePickerFileType("LogLens workspace") { Patterns = ["*.json"] }]
        });

        var local = files.FirstOrDefault()?.TryGetLocalPath();
        if (local is null) return;

        try
        {
            _vm.Save();
            _vm.Open(local);
            Notify("Workspace opened");
        }
        catch (Exception ex) { Notify(ex.Message); }
    }

    private void SaveWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Save();
            Notify("Saved " + Path.GetFileName(_vm.WorkspacePath));
        }
        catch (Exception ex) { Notify(ex.Message); }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void AddView_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptWindow.Ask(this, "Add view", "Name for the new view:", "New view");
        if (!string.IsNullOrWhiteSpace(name)) _vm.AddView(name.Trim());
    }

    private async void EditView_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null) return;
        await new ViewEditWindow(View).ShowDialog(this);
        _vm.Dirty = true;
    }

    private void RemoveView_Click(object? sender, RoutedEventArgs e)
    {
        if (View is not null) _vm.RemoveView(View);
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null) { Notify("Add a view first"); return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add log files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Log files") { Patterns = ["*.log", "*.txt", "*.out", "*.err"] },
                FilePickerFileTypes.All
            ]
        });

        int added = 0;
        foreach (var f in files)
        {
            var local = f.TryGetLocalPath();
            if (local is null) continue;
            View.AddSource(new LogSource { Path = local });
            added++;
        }

        if (added > 0) { _vm.Dirty = true; Notify($"Added {added} file(s)"); }
    }

    private async void AddWildcard_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null) { Notify("Add a view first"); return; }

        var spec = await PromptWindow.Ask(this, "Add wildcard path",
            "Path with * or ? — the newest matching file is tailed:", "");
        if (string.IsNullOrWhiteSpace(spec)) return;

        View.AddSource(new LogSource { Path = spec.Trim() });
        _vm.Dirty = true;
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null || Pane is null) return;
        View.RemoveTab(Pane);
        _vm.Dirty = true;
    }

    private void ToggleMerged_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null) return;
        View.Def.ShowMergedTimeline = !View.Def.ShowMergedTimeline;
        _vm.Dirty = true;
    }

    private async void Issues_Click(object? sender, RoutedEventArgs e)
        => await new IssuesWindow(_issues, _vm.Settings).ShowDialog(this);

    private async void Rules_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new RulesWindow(_vm.GlobalRules);
        await dlg.ShowDialog(this);
        if (!dlg.Accepted) return;

        _vm.GlobalRules.Clear();
        _vm.GlobalRules.AddRange(dlg.Result);
        _vm.ReapplyRulesEverywhere();
        Notify("Highlight rules updated");
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow(_vm.Settings).ShowDialog(this);
        _vm.Dirty = true;
    }

    private void About_Click(object? sender, RoutedEventArgs e)
        => Notify($"LogLens (Avalonia) {UpdateService.CurrentVersion} — early cross-platform build");
}
