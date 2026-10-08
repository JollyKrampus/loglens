using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private bool _formatHintShown;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DesktopAlerts _alerts;

    // Find-in-tab: one bar for the window, acting on the selected pane. The logic
    // is LogLens.Core's LineFinder, the same one the WPF pane uses.
    private readonly LineFinder _finder = new();
    private LogPaneVm? _findPane;

    /// <summary>The realised log ListBox. TabControl keeps one and swaps its DataContext.</summary>
    private ListBox? _lines;

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
        _vm.FormatHint += m => { if (!_formatHintShown) { _formatHintShown = true; Notify(m); } };
        DataContext = _vm;

        var dbPath = Path.Combine(
            Path.GetDirectoryName(_vm.WorkspacePath) ?? WorkspaceStore.RoamingDirectory,
            "loglens.issues.db");
        _issues = new IssueRecorder(new IssueStore(dbPath), _vm.Settings);
        _vm.LinesIngested += (view, tab, lines) => _issues.Observe(view.Name, tab.Header, lines);

        // Same decision as the WPF app (AlertPolicy in Core); macOS delivery.
        _alerts = new DesktopAlerts(_vm.Alerts);
        _vm.AlertsDetected += (view, tab, lines) =>
            _alerts.Consider(IsActive, view.Name, view.Def.AlertsEnabled, tab.Header, lines);

        WireFind();

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

        _lines = list;

        void Wire()
        {
            if (list.DataContext is not LogPaneVm pane) return;

            // A different pane under an open find bar: recount against it.
            if (FindBar.IsVisible) RefreshFind();

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

    private void LinesList_Detached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(sender, _lines)) _lines = null;
    }

    /// <summary>The live ListBox, but only if it is currently showing <paramref name="pane"/>.</summary>
    private ListBox? ListFor(LogPaneVm? pane)
        => pane is not null && _lines is { } l && ReferenceEquals(l.DataContext, pane) ? l : null;

    private void Clear_Click(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as LogPaneVm)?.ClearBuffer();

    private void Reload_Click(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as LogPaneVm)?.ReloadFromDisk();

    // ---- find in tab -------------------------------------------------------------

    private KeyGesture? _findGesture, _findNextGesture, _findPrevGesture;

    /// <summary>
    /// Shortcuts use the platform's command modifier — Cmd on macOS, Ctrl elsewhere —
    /// taken from Avalonia's hotkey configuration rather than hard-coded, so the Mac
    /// build gets Cmd+F / Cmd+G / Shift+Cmd+G and Windows/Linux get Ctrl. F3 and
    /// Shift+F3 work everywhere. Handled on the tunnel so a focused filter box or
    /// the log list can't swallow them first.
    /// </summary>
    private void WireFind()
    {
        var cmd = PlatformSettings?.HotkeyConfiguration.CommandModifiers
                  ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

        _findGesture = new KeyGesture(Key.F, cmd);
        _findNextGesture = new KeyGesture(Key.G, cmd);
        _findPrevGesture = new KeyGesture(Key.G, cmd | KeyModifiers.Shift);
        FindMenuItem.InputGesture = _findGesture;

        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);

        FindBox.TextChanged += (_, __) => RefreshFind();
        FindRegex.IsCheckedChanged += (_, __) => RefreshFind();
        FindCase.IsCheckedChanged += (_, __) => RefreshFind();

        // Enter / Shift+Enter step, Escape closes — as in the WPF find bar.
        FindBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindStep(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideFind();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (_findGesture?.Matches(e) == true) { ShowFind(); e.Handled = true; }
        else if (_findPrevGesture?.Matches(e) == true
                 || (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.Shift)) { FindStep(-1); e.Handled = true; }
        else if (_findNextGesture?.Matches(e) == true
                 || (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.None)) { FindStep(+1); e.Handled = true; }
    }

    private void ShowFind_Click(object? sender, RoutedEventArgs e) => ShowFind();
    private void HideFind_Click(object? sender, RoutedEventArgs e) => HideFind();
    private void FindNext_Click(object? sender, RoutedEventArgs e) => FindStep(+1);
    private void FindPrev_Click(object? sender, RoutedEventArgs e) => FindStep(-1);

    private void ShowFind()
    {
        FindBar.IsVisible = true;
        FindBox.Focus();
        FindBox.SelectAll();
        RefreshFind();
    }

    private void HideFind()
    {
        FindBar.IsVisible = false;
        ListFor(Pane)?.Focus();
    }

    private bool FindRegexOn => FindRegex.IsChecked == true;
    private bool FindCaseOn => FindCase.IsChecked == true;

    private void SyncFindPane()
    {
        if (ReferenceEquals(_findPane, Pane)) return;
        _findPane = Pane;
        _finder.Reset();
    }

    private void RefreshFind()
    {
        SyncFindPane();
        _finder.Search(Pane?.Display, FindBox.Text, FindRegexOn, FindCaseOn);
        FindStatus.Text = _finder.Status;
    }

    private void FindStep(int direction)
    {
        var pane = Pane;
        if (pane is null) return;
        if (!FindBar.IsVisible) { ShowFind(); return; }

        SyncFindPane();
        var list = ListFor(pane);
        int target = _finder.Step(pane.Display, FindBox.Text, FindRegexOn, FindCaseOn,
                                  list?.SelectedIndex ?? -1, direction);
        FindStatus.Text = _finder.Status;
        if (target < 0 || list is null) return;

        // Jumping to a hit means you want to read it, not be dragged back to the tail.
        pane.FollowTail = false;
        list.SelectedIndex = target;
        list.ScrollIntoView(target);
    }

    // ---- editor / file manager ----------------------------------------------------

    // Resolve + launch run off the UI thread, as in the WPF app: resolving a wildcard
    // and probing the file both hit the file system, and an unreachable network
    // mount can block for its whole timeout.

    /// <summary>
    /// The file a pane's editor/reveal actions target: the tab's own file, or — on the
    /// merged timeline, which has no single file — the SELECTED LINE's source file.
    /// </summary>
    private LogTab? ResolveTargetTab(LogPaneVm? pane, out string? whyNot)
    {
        whyNot = null;

        if (pane is LogTab file) return file;

        if (pane is MergedTab merged)
        {
            if (ListFor(merged)?.SelectedItem is not LogLine line)
            {
                whyNot = "Select a line first — in the merged view these act on that line's file.";
                return null;
            }

            var source = merged.SourceTabFor(line);
            if (source is null) whyNot = "That line's file tab is no longer in this view.";
            return source;
        }

        whyNot = "No log file here.";
        return null;
    }

    private async Task OpenInEditor(LogTab tab, bool confirm)
    {
        var error = await Task.Run(() => ShellOpen.OpenInEditor(tab.ResolvedFilePath));
        if (error is not null) Notify(error);
        else if (confirm) Notify("Opened in editor");
    }

    private async Task Reveal(LogTab tab)
    {
        var error = await Task.Run(() => ShellOpen.Reveal(tab.ResolvedFilePath));
        if (error is not null) Notify(error);
    }

    /// <summary>Main menu: the selected tab's file. The merged view has none.</summary>
    private LogTab? CurrentFileTab()
    {
        if (Pane is LogTab tab) return tab;
        Notify(Pane is MergedTab ? "The merged view spans several files — use a file tab" : "No log file selected");
        return null;
    }

    private async void OpenCurrentInEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentFileTab() is { } tab) await OpenInEditor(tab, confirm: true);
    }

    private async void RevealCurrent_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentFileTab() is { } tab) await Reveal(tab);
    }

    private async void PaneOpenInEditor_Click(object? sender, RoutedEventArgs e)
    {
        var tab = ResolveTargetTab((sender as Control)?.DataContext as LogPaneVm, out var whyNot);
        if (tab is null) { if (whyNot is not null) Notify(whyNot); return; }
        await OpenInEditor(tab, confirm: false);
    }

    private async void ContextOpenInEditor_Click(object? sender, RoutedEventArgs e)
    {
        var tab = ResolveTargetTab((sender as Control)?.DataContext as LogPaneVm ?? Pane, out var whyNot);
        if (tab is null) { if (whyNot is not null) Notify(whyNot); return; }
        await OpenInEditor(tab, confirm: false);
    }

    private async void ContextReveal_Click(object? sender, RoutedEventArgs e)
    {
        var tab = ResolveTargetTab((sender as Control)?.DataContext as LogPaneVm ?? Pane, out var whyNot);
        if (tab is null) { if (whyNot is not null) Notify(whyNot); return; }
        await Reveal(tab);
    }

    private void GoToSourceTab_Click(object? sender, RoutedEventArgs e)
    {
        if (((sender as Control)?.DataContext as LogPaneVm ?? Pane) is not MergedTab merged) return;

        if (ListFor(merged)?.SelectedItem is not LogLine line) { Notify("Select a line first."); return; }

        var source = merged.SourceTabFor(line);
        if (source is null) { Notify("That line's file tab is no longer in this view."); return; }

        merged.NavigateToSourceRequested?.Invoke(source, line);
    }

    // ---- alerts menu --------------------------------------------------------------

    private void ToggleAlertsEnabled_Click(object? sender, RoutedEventArgs e)
    { _vm.Alerts.Enabled = !_vm.Alerts.Enabled; _vm.Dirty = true; }

    private void ToggleViewAlerts_Click(object? sender, RoutedEventArgs e)
    {
        if (View is null) return;
        View.Def.AlertsEnabled = !View.Def.AlertsEnabled;
        _vm.Dirty = true;
    }

    private void ToggleShowToast_Click(object? sender, RoutedEventArgs e)
    { _vm.Alerts.ShowToast = !_vm.Alerts.ShowToast; _vm.Dirty = true; }

    private void TogglePlaySound_Click(object? sender, RoutedEventArgs e)
    { _vm.Alerts.PlaySound = !_vm.Alerts.PlaySound; _vm.Dirty = true; }

    private void ToggleOnlyUnfocused_Click(object? sender, RoutedEventArgs e)
    { _vm.Alerts.OnlyWhenUnfocused = !_vm.Alerts.OnlyWhenUnfocused; _vm.Dirty = true; }

    private async void AlertSettings_Click(object? sender, RoutedEventArgs e)
    {
        await new AlertsWindow(_vm.Alerts, _alerts).ShowDialog(this);
        _vm.Dirty = true;
    }

    private void TestAlert_Click(object? sender, RoutedEventArgs e)
    {
        _alerts.SendTestAlert();
        Notify(DesktopAlerts.CanNotify || DesktopAlerts.CanPlaySound
            ? "Test alert sent"
            : "Alerts are not delivered on this platform");
    }

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
