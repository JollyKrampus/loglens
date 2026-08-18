using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LogLens.Core;
using LogLens.Dialogs;
using LogLens.Models;
using LogLens.Services;
using LogLens.ViewModels;

namespace LogLens;

public partial class MainWindow : Window
{
    private readonly MainVm _vm;
    private readonly DispatcherTimer _toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        var path = WorkspaceStore.DefaultPath;
        Workspace ws;
        try
        {
            ws = WorkspaceStore.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{ex.Message}\n\nStarting with a fresh workspace.",
                "LogLens", MessageBoxButton.OK, MessageBoxImage.Warning);
            ws = Workspace.CreateDefault();
        }

        _vm = new MainVm(ws, path, new WpfUiThread());
        _vm.UserMessage += m => MessageBox.Show(m, "LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
        DataContext = _vm;

        App.ApplyTheme(_vm.Settings.LightTheme);
        _vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.LightTheme))
                App.ApplyTheme(_vm.Settings.LightTheme);
            _vm.Dirty = true;
        };

        RestoreWindowPlacement(ws);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, __) => { Toast.Text = ""; _toastTimer.Stop(); };

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, __) => SaveWorkspace()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, __) => OpenWorkspace()));

        _alerts = new AlertService(_vm.Alerts);
        _alerts.AlertActivated += OnAlertActivated;
        _vm.AlertsDetected += OnAlertsDetected;

        // The issue database lives beside the workspace, so a portable install keeps
        // its history on the same stick.
        var dbPath = Path.Combine(
            Path.GetDirectoryName(_vm.WorkspacePath) ?? WorkspaceStore.RoamingDirectory,
            "loglens.issues.db");

        _issues = new IssueRecorder(new IssueStore(dbPath), _vm.Settings);
        _vm.LinesIngested += (view, tab, lines) => _issues.Observe(view.Name, tab.Header, lines);

        // A previous self-update may have left LogLens.exe.old behind.
        UpdateService.CleanUpLeftovers();

        StartLoadingProgress();
        Loaded += (_, __) => _ = QuietUpdateCheckAsync();

        // Auto-save: a few seconds after anything marks the workspace dirty — views,
        // rules, chips, filters — it lands on disk without a Ctrl+S. The close-time
        // save remains regardless.
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoSaveTimer.Tick += (_, __) =>
        {
            if (!_vm.Dirty || !_vm.Settings.AutoSaveWorkspace) return;
            try
            {
                CaptureWindowPlacement();
                _vm.Save();
            }
            catch { /* a transiently locked file just waits for the next tick */ }
        };
        _autoSaveTimer.Start();

        Closing += OnClosing;
    }

    private readonly DispatcherTimer _autoSaveTimer;

    // ================= startup loading progress =================

    private DispatcherTimer? _loadingTimer;

    /// <summary>
    /// Shows "Opening logs… n/m" in the status bar while the initial tail reads are
    /// running — with dozens of large files that phase is what makes startup feel
    /// slow. Polling beats wiring per-tab events for something this transient.
    /// </summary>
    private void StartLoadingProgress()
    {
        _loadingTimer?.Stop();

        var startedAt = DateTime.UtcNow;
        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _loadingTimer.Tick += (_, __) =>
        {
            var tabs = _vm.Views.SelectMany(v => v.FileTabs).ToList();
            int total = tabs.Count;
            int primed = tabs.Count(t => t.IsPrimed);

            bool done = total == 0 || primed >= total
                        || DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(2);

            if (done)
            {
                _loadingTimer!.Stop();
                LoadingPanel.Visibility = Visibility.Collapsed;
                return;
            }

            LoadingPanel.Visibility = Visibility.Visible;
            LoadingBar.Maximum = total;
            LoadingBar.Value = primed;
            LoadingText.Text = $"Opening logs… {primed}/{total}";
        };
        _loadingTimer.Start();
    }

    // ================= updates =================

    private async Task QuietUpdateCheckAsync()
    {
        if (!_vm.Settings.CheckForUpdates) return;

        try
        {
            // Let the tailers claim startup I/O first.
            await Task.Delay(TimeSpan.FromSeconds(5));
            var update = await UpdateService.CheckAsync();
            if (update is not null)
                Notify($"LogLens {update.Latest} is available — Help ▸ Check for updates");
        }
        catch
        {
            // Offline, rate-limited, GitHub down: a quiet check stays quiet.
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Notify("Checking…");
            var update = await UpdateService.CheckAsync();

            if (update is null)
            {
                Notify($"You're on the latest version ({UpdateService.CurrentVersion})");
                return;
            }

            Notify("");
            var dlg = new UpdateWindow(update) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Notify("Update check failed: " + ex.Message);
        }
    }

    private readonly AlertService _alerts;
    private readonly IssueRecorder _issues;

    /// <summary>A file tab took on error-level lines; let the alert service decide what to do.</summary>
    private void OnAlertsDetected(ViewVm view, LogTab tab, IReadOnlyList<LogLine> lines)
        => _alerts.Consider(this, view.Name, view.Def.AlertsEnabled, tab.Header, lines);

    /// <summary>Clicking the notification jumps to the view that raised it.</summary>
    private void OnAlertActivated(string viewName)
    {
        var target = _vm.Views.FirstOrDefault(v => v.Name == viewName);
        if (target is not null) _vm.SelectedView = target;

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private ViewVm? View => _vm.SelectedView;

    /// <summary>The selected pane, which may be the merged timeline.</summary>
    private LogPaneVm? Pane => _vm.SelectedView?.SelectedTab;

    /// <summary>The selected pane only when it is a real file — null on the merged tab.</summary>
    private LogTab? Tab => _vm.SelectedView?.SelectedTab as LogTab;

    private void Notify(string message)
    {
        Toast.Text = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ================= window placement =================

    private void RestoreWindowPlacement(Workspace ws)
    {
        if (ws.WindowWidth is > 200 && ws.WindowHeight is > 200)
        {
            Width = ws.WindowWidth.Value;
            Height = ws.WindowHeight.Value;
        }

        if (ws.WindowLeft is { } l && ws.WindowTop is { } t)
        {
            // Only restore if that spot is still on a connected monitor.
            var area = SystemParameters.VirtualScreenWidth;
            if (l > -50 && t > -50 && l < area && t < SystemParameters.VirtualScreenHeight)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }
        }

        if (ws.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void CaptureWindowPlacement()
    {
        var ws = _vm.Workspace;
        ws.WindowMaximized = WindowState == WindowState.Maximized;

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width > 0)
        {
            ws.WindowLeft = bounds.Left;
            ws.WindowTop = bounds.Top;
            ws.WindowWidth = bounds.Width;
            ws.WindowHeight = bounds.Height;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CaptureWindowPlacement();

        // Views are the whole point of the app — never lose them to a stray Alt+F4.
        try { _vm.Save(); }
        catch (Exception ex)
        {
            var r = MessageBox.Show(
                $"Could not save the workspace:\n\n{ex.Message}\n\nClose anyway?",
                "LogLens", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.No) { e.Cancel = true; return; }
        }

        _alerts.Dispose();
        _issues.Dispose();      // flushes anything still queued
        _vm.Dispose();
    }

    // ================= file menu =================

    private void NewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Start a new workspace? The current one is saved first.",
                "LogLens", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try { _vm.Save(); } catch { /* best effort */ }
        _vm.NewWorkspace();
        StartLoadingProgress();
        Notify("New workspace");
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e) => OpenWorkspace();

    private void OpenWorkspace()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open workspace",
            Filter = "LogLens workspace (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = SafeDirectory(_vm.WorkspacePath)
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _vm.Save();
            _vm.Open(dlg.FileName);
            App.ApplyTheme(_vm.Settings.LightTheme);
            StartLoadingProgress();
            Notify("Workspace opened");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LogLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e) => SaveWorkspace();

    private void SaveWorkspace()
    {
        CaptureWindowPlacement();
        try
        {
            _vm.Save();
            Notify("Saved " + Path.GetFileName(_vm.WorkspacePath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LogLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveWorkspaceAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save workspace as",
            Filter = "LogLens workspace (*.json)|*.json",
            FileName = Path.GetFileName(_vm.WorkspacePath),
            InitialDirectory = SafeDirectory(_vm.WorkspacePath)
        };
        if (dlg.ShowDialog(this) != true) return;

        CaptureWindowPlacement();
        try
        {
            _vm.SaveAs(dlg.FileName);
            Notify("Saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LogLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = SafeDirectory(_vm.WorkspacePath);
        if (dir is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            var d = Path.GetDirectoryName(path);
            return Directory.Exists(d) ? d : null;
        }
        catch { return null; }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ================= view menu =================

    private void AddView_Click(object sender, RoutedEventArgs e)
    {
        var def = new ViewDef { Name = "New view" };
        var dlg = new ViewEditWindow(def) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var vm = _vm.AddView(def.Name);
        vm.Def.Accent = def.Accent;
        foreach (var s in def.Sources) vm.AddSource(s);
        Notify($"Added view '{def.Name}'");
    }

    private void EditView_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) return;

        var dlg = new ViewEditWindow(View.Def) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        SyncTabsWithSources(View);
        _vm.Dirty = true;
    }

    private void ViewList_DoubleClick(object sender, MouseButtonEventArgs e) => EditView_Click(sender, e);

    /// <summary>
    /// The view editor edits Def.Sources directly, so reconcile the live tabs
    /// against it: close tabs whose source is gone, open tabs for new sources.
    /// </summary>
    private void SyncTabsWithSources(ViewVm view)
    {
        foreach (var tab in view.FileTabs.ToList())
        {
            if (!view.Def.Sources.Contains(tab.Source))
            {
                view.Tabs.Remove(tab);
                tab.Dispose();
            }
        }

        foreach (var src in view.Def.Sources.ToList())
        {
            if (view.FileTabs.Any(t => t.Source == src)) continue;
            view.Def.Sources.Remove(src);
            view.AddSource(src);
        }

        view.SelectedTab ??= view.Tabs.FirstOrDefault();
        view.RefreshBadges();
    }

    private void DuplicateView_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) return;
        _vm.DuplicateView(View);
        Notify("View duplicated");
    }

    private void RemoveView_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) return;
        if (MessageBox.Show($"Remove the view '{View.Name}'?\n\nYour log files are not touched.",
                "LogLens", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _vm.RemoveView(View);
    }

    private void MoveViewUp_Click(object sender, RoutedEventArgs e)
    {
        if (View is not null) _vm.MoveView(View, -1);
    }

    private void MoveViewDown_Click(object sender, RoutedEventArgs e)
    {
        if (View is not null) _vm.MoveView(View, +1);
    }

    // ================= files menu =================

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) { Notify("Add a view first"); return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add log files",
            Multiselect = true,
            Filter = "Log files (*.log;*.txt;*.out;*.err)|*.log;*.txt;*.out;*.err|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames) View.AddSource(new LogSource { Path = f });
        _vm.Dirty = true;
        Notify($"Added {dlg.FileNames.Length} file(s)");
    }

    private void AddWildcard_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) { Notify("Add a view first"); return; }

        var dlg = new PromptWindow(
            "Add wildcard path",
            @"Path with * or ? — the newest matching file is tailed, and the tab follows the roll." + "\n" +
            @"Example:  \\prod-app01\logs\service-*.log",
            "") { Owner = this };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;

        View.AddSource(new LogSource { Path = dlg.Value.Trim() });
        _vm.Dirty = true;
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null) return;

        var dlg = new PromptWindow("Rename tab", "Display name for this tab:", Tab.Header) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        Tab.Source.Name = dlg.Value.Trim();
        _vm.Dirty = true;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (View is null || Tab is null) return;
        View.RemoveTab(Tab);
        _vm.Dirty = true;
    }

    // Off the UI thread: resolving a wildcard and probing the file both hit the file
    // system, and an unreachable network share blocks for the full SMB timeout.

    private async void OpenCurrentInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null)
        {
            Notify(Pane is MergedTab ? "The merged view spans several files — use a file tab" : "No log file selected");
            return;
        }

        var tab = Tab;
        var error = await Task.Run(() => ShellOpen.OpenInEditor(tab.ResolvedFilePath));
        Notify(error ?? "Opened in editor");
    }

    private async void RevealCurrentInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is null)
        {
            Notify(Pane is MergedTab ? "The merged view spans several files — use a file tab" : "No log file selected");
            return;
        }

        var tab = Tab;
        var error = await Task.Run(() => ShellOpen.RevealInExplorer(tab.ResolvedFilePath));
        if (error is not null) Notify(error);
    }

    private void ReloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) return;
        foreach (var t in View.Tabs) t.ReloadFromDisk();
        Notify("Reloading");
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (View is null) return;
        foreach (var t in View.Tabs) t.ClearBuffer();
        Notify("Buffers cleared");
    }

    // ================= tools menu =================

    private void Rules_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RulesWindow(_vm.GlobalRules, View?.Def) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _vm.GlobalRules.Clear();
        _vm.GlobalRules.AddRange(dlg.ResultGlobalRules);

        if (View is not null)
        {
            View.Def.Rules.Clear();
            View.Def.Rules.AddRange(dlg.ResultViewRules);
        }

        _vm.ReapplyRulesEverywhere();
        Notify("Highlight rules updated");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm.Settings) { Owner = this };
        dlg.ShowDialog();
        _vm.Dirty = true;
    }

    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new IssuesWindow(_issues, _vm.Settings) { Owner = this };
        dlg.ShowDialog();
    }

    private void AlertSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AlertsWindow(_vm.Alerts, _alerts, this) { Owner = this };
        dlg.ShowDialog();
        _vm.Dirty = true;
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        _alerts.SendTestAlert(this);
        Notify("Test alert sent");
    }

    // ================= help =================

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            """
            Getting around

              Ctrl+F        Find in the current tab (Enter / Shift+Enter to step)
              Ctrl+S        Save the workspace
              Ctrl+O        Open a workspace
              Ctrl+C        Copy the selected lines
              Ctrl+A        Select all

            Tailing

              Follow auto-scrolls as lines arrive. Scrolling up switches it off,
              scrolling back to the bottom switches it on again.

              Pause holds new lines without dropping them — unpause and they appear.

              Clear empties the on-screen buffer only. Your log file is never written to.

            Views

              A view is a saved group of log files, such as Dev, Test or Prod.
              Views, filters, highlight rules and window size all live in the
              workspace JSON next to the exe, so the whole thing is portable.

            Wildcards

              A path such as C:\logs\app-*.log tails whichever match was written
              most recently, and follows the roll to tomorrow's file on its own.

            Highlighting

              Rules are evaluated top to bottom and the first match wins, so keep
              FATAL above ERROR. Reorder them in Tools > Highlight rules.
            """,
            "LogLens — shortcuts and tips", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        // Read the version from the assembly so it cannot drift from the .csproj.
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        // Single-file builds append a source-revision suffix; trim it.
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        MessageBox.Show(
            $"""
            LogLens {version}

            A portable real-time log viewer for Windows.
            No install, no service, no account, no telemetry.

            Workspace: {_vm.WorkspacePath}
            """,
            "About LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================= drag and drop =================

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && View is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (View is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            View.AddSource(new LogSource { Path = f });
            added++;
        }

        if (added > 0)
        {
            _vm.Dirty = true;
            Notify($"Added {added} file(s) to '{View.Name}'");
        }
    }
}
