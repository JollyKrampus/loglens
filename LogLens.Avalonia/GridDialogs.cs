using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Avalonia;

/// <summary>Global highlight rules, edited on clones so Cancel really cancels.</summary>
public sealed class RulesWindow : Window
{
    private readonly List<HighlightRule> _rules;
    private readonly DataGrid _grid;

    public bool Accepted { get; private set; }
    public IReadOnlyList<HighlightRule> Result => _rules;

    public RulesWindow(IEnumerable<HighlightRule> globalRules)
    {
        Title = "Highlight rules";
        Width = 980;
        Height = 520;
        Background = Ui.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _rules = globalRules.Select(r => r.Clone()).ToList();

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserReorderColumns = false,
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<HighlightRule>(_rules),
            Columns =
            {
                new DataGridCheckBoxColumn { Header = "On", Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Enabled)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridTextColumn { Header = "Name", Width = new DataGridLength(120), Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Name)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridTextColumn { Header = "Pattern", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Pattern)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridCheckBoxColumn { Header = ".*", Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.IsRegex)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridCheckBoxColumn { Header = "B", Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Bold)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridTextColumn { Header = "Text (hex)", Width = new DataGridLength(100), Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Foreground)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
                new DataGridTextColumn { Header = "Back (hex)", Width = new DataGridLength(100), Binding = new global::Avalonia.Data.Binding(nameof(HighlightRule.Background)) { Mode = global::Avalonia.Data.BindingMode.TwoWay } },
            }
        };

        var add = new Button { Content = "Add rule" };
        add.Click += (_, __) =>
        {
            var r = new HighlightRule { Name = "New rule", Pattern = "" };
            _rules.Add(r);
            ((System.Collections.ObjectModel.ObservableCollection<HighlightRule>)_grid.ItemsSource!).Add(r);
        };

        var remove = new Button { Content = "Delete selected" };
        remove.Click += (_, __) =>
        {
            if (_grid.SelectedItem is HighlightRule r)
            {
                _rules.Remove(r);
                ((System.Collections.ObjectModel.ObservableCollection<HighlightRule>)_grid.ItemsSource!).Remove(r);
            }
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, __) => { Accepted = true; Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, __) => Close();

        // Preset loading, same registry the WPF app uses — replaces the whole list.
        var presets = new ComboBox
        {
            PlaceholderText = "Load preset…",
            MinWidth = 220,
            ItemsSource = RulePresets.All.Select(p => p.Name).ToList(),
        };
        presets.SelectionChanged += (_, __) =>
        {
            if (presets.SelectedItem is not string name) return;
            var preset = RulePresets.All.FirstOrDefault(p => p.Name == name);
            if (preset is null) return;

            _rules.Clear();
            _rules.AddRange(preset.Build());

            var items = (System.Collections.ObjectModel.ObservableCollection<HighlightRule>)_grid.ItemsSource!;
            items.Clear();
            foreach (var r in _rules) items.Add(r);
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(add);
        tools.Children.Add(remove);
        tools.Children.Add(presets);
        tools.Children.Add(new TextBlock
        {
            Text = "First match wins — keep FATAL above ERROR.",
            Foreground = Ui.Dim,
            VerticalAlignment = VerticalAlignment.Center
        });

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = Ui.Buttons(cancel, ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(tools, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tools);
        root.Children.Add(_grid);

        Content = root;
    }
}

/// <summary>The per-view issue database, read-only browsing plus the Jira copy.</summary>
public sealed class IssuesWindow : Window
{
    public IssuesWindow(IssueRecorder recorder, AppSettings settings)
    {
        Title = "Issues";
        Width = 1050;
        Height = 560;
        Background = Ui.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        recorder.Flush();
        var rows = recorder.Store.Query(limit: 2000);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            ItemsSource = rows,
            Columns =
            {
                new DataGridTextColumn { Header = "Sev", Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.Severity)) },
                new DataGridTextColumn { Header = "View", Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.View)) },
                new DataGridTextColumn { Header = "Count", Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.Count)) },
                new DataGridTextColumn { Header = "Title", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.Title)) },
                new DataGridTextColumn { Header = "Last seen", Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.LastSeenLocal)) },
                new DataGridTextColumn { Header = "Jira", Binding = new global::Avalonia.Data.Binding(nameof(LogIssue.JiraKey)) },
            }
        };

        var copy = new Button { Content = "Copy Jira ticket" };
        copy.Click += async (_, __) =>
        {
            if (grid.SelectedItem is LogIssue issue && Clipboard is { } cb)
                await cb.SetTextAsync(JiraTemplate.Full(issue, settings.JiraProjectKey));
        };

        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
        close.Click += (_, __) => Close();

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = Ui.Buttons(copy, close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(grid);

        Content = root;
    }
}
