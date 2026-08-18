using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Avalonia;

// The dialogs are built in code rather than AXAML on purpose: they are small,
// form-like, and code keeps them terse. The main window, with its templates and
// styles, stays in AXAML where that pays off.

internal static class Ui
{
    public static readonly IBrush Bg = new ImmutableSolidColorBrush(Color.Parse("#1E1E1E"));
    public static readonly IBrush Dim = new ImmutableSolidColorBrush(Color.Parse("#9A9A9A"));

    public static Window Shell(string title, double width, Control content)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            Background = Bg,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Thickness(16), Child = content }
        };
    }

    public static TextBlock Label(string text) => new()
    { Text = text, Foreground = Dim, Margin = new Thickness(0, 8, 0, 2) };

    public static StackPanel Buttons(params Control[] buttons)
    {
        var p = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0)
        };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }
}

/// <summary>One-line input dialog. Returns null on cancel.</summary>
public static class PromptWindow
{
    public static async Task<string?> Ask(Window owner, string title, string message, string initial)
    {
        var input = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = message, Foreground = Ui.Dim, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(Ui.Buttons(cancel, ok));

        var dlg = Ui.Shell(title, 480, panel);
        string? result = null;
        ok.Click += (_, __) => { result = input.Text; dlg.Close(); };
        cancel.Click += (_, __) => dlg.Close();

        await dlg.ShowDialog(owner);
        return result;
    }
}

/// <summary>Name, accent and source list for one view.</summary>
public sealed class ViewEditWindow : Window
{
    private readonly LogLens.ViewModels.ViewVm _view;
    private readonly ListBox _list;
    private readonly TextBox _name;
    private readonly TextBox _accent;

    public ViewEditWindow(LogLens.ViewModels.ViewVm view)
    {
        _view = view;
        Title = "Edit view";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Background = Ui.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name = new TextBox { Text = view.Def.Name };
        _accent = new TextBox { Text = view.Def.Accent, Width = 100 };
        _list = new ListBox
        {
            Height = 220,
            ItemsSource = view.Def.Sources.Select(s => $"{s.Name}  —  {s.Path}").ToList()
        };

        var addFiles = new Button { Content = "Add files…" };
        var addWildcard = new Button { Content = "Add wildcard…" };
        var remove = new Button { Content = "Remove selected" };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };

        addFiles.Click += async (_, __) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Add log files", AllowMultiple = true });
            foreach (var f in files)
                if (f.TryGetLocalPath() is { } p)
                    _view.AddSource(new LogSource { Path = p });
            RefreshList();
        };

        addWildcard.Click += async (_, __) =>
        {
            var spec = await PromptWindow.Ask(this, "Add wildcard path",
                "Path with * or ? — the newest match is tailed:", "");
            if (!string.IsNullOrWhiteSpace(spec))
            {
                _view.AddSource(new LogSource { Path = spec.Trim() });
                RefreshList();
            }
        };

        remove.Click += (_, __) =>
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _view.Def.Sources.Count) return;
            var tab = _view.FileTabs.FirstOrDefault(t => t.Source == _view.Def.Sources[i]);
            if (tab is not null) _view.RemoveTab(tab);
            RefreshList();
        };

        close.Click += (_, __) =>
        {
            _view.Def.Name = _name.Text?.Trim() is { Length: > 0 } n ? n : _view.Def.Name;
            _view.Def.Accent = _accent.Text?.Trim() is { Length: > 0 } a ? a : _view.Def.Accent;
            Close();
        };

        var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        accentRow.Children.Add(_accent);
        accentRow.Children.Add(new TextBlock
        { Text = "hex colour for the sidebar stripe", Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center });

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttonRow.Children.Add(addFiles);
        buttonRow.Children.Add(addWildcard);
        buttonRow.Children.Add(remove);

        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("View name"));
        panel.Children.Add(_name);
        panel.Children.Add(Ui.Label("Accent"));
        panel.Children.Add(accentRow);
        panel.Children.Add(Ui.Label("Log files (changes apply immediately)"));
        panel.Children.Add(_list);
        panel.Children.Add(buttonRow);
        panel.Children.Add(Ui.Buttons(close));

        Content = new Border { Padding = new Thickness(16), Child = panel };
    }

    private void RefreshList()
        => _list.ItemsSource = _view.Def.Sources.Select(s => $"{s.Name}  —  {s.Path}").ToList();
}

/// <summary>The subset of settings that matter cross-platform. Edits apply live.</summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        Title = "Settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        Background = Ui.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var poll = Num(settings.PollIntervalMs, v => settings.PollIntervalMs = v);
        var tailKb = Num(settings.InitialTailKb, v => settings.InitialTailKb = v);
        var maxLines = Num(settings.MaxLines, v => settings.MaxLines = v);
        var autoSave = Check("Auto-save the workspace", settings.AutoSaveWorkspace, v => settings.AutoSaveWorkspace = v);
        var track = Check("Track issues in the local database", settings.TrackIssues, v => settings.TrackIssues = v);
        var jiraUrl = new TextBox { Text = settings.JiraBaseUrl };
        jiraUrl.LostFocus += (_, __) => settings.JiraBaseUrl = jiraUrl.Text ?? "";
        var jiraKey = new TextBox { Text = settings.JiraProjectKey, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        jiraKey.LostFocus += (_, __) => settings.JiraProjectKey = jiraKey.Text ?? "";

        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
        close.Click += (_, __) => Close();

        var panel = new StackPanel();
        panel.Children.Add(Ui.Label("Check for new lines every (ms)"));
        panel.Children.Add(poll);
        panel.Children.Add(Ui.Label("Load at most (KB) when opening — 0 loads everything"));
        panel.Children.Add(tailKb);
        panel.Children.Add(Ui.Label("Keep at most (lines) per tab"));
        panel.Children.Add(maxLines);
        panel.Children.Add(autoSave);
        panel.Children.Add(track);
        panel.Children.Add(Ui.Label("Jira URL"));
        panel.Children.Add(jiraUrl);
        panel.Children.Add(Ui.Label("Jira project key"));
        panel.Children.Add(jiraKey);
        panel.Children.Add(Ui.Buttons(close));

        Content = new Border { Padding = new Thickness(16), Child = panel };
    }

    private static TextBox Num(int initial, Action<int> apply)
    {
        var box = new TextBox { Text = initial.ToString(), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        box.LostFocus += (_, __) => { if (int.TryParse(box.Text, out var v)) apply(v); };
        return box;
    }

    private static CheckBox Check(string text, bool initial, Action<bool> apply)
    {
        var cb = new CheckBox { Content = text, IsChecked = initial, Margin = new Thickness(0, 8, 0, 0) };
        cb.IsCheckedChanged += (_, __) => apply(cb.IsChecked == true);
        return cb;
    }
}
