using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Dialogs;

public partial class RulesWindow : Window
{
    private readonly ObservableCollection<HighlightRule> _global;
    private readonly ObservableCollection<HighlightRule> _view;

    public RulesWindow(IEnumerable<HighlightRule> globalRules, ViewDef? view)
    {
        InitializeComponent();

        // Everything is edited on clones, so Cancel leaves the live rules untouched.
        _global = new ObservableCollection<HighlightRule>(globalRules.Select(r => r.Clone()));
        _view = new ObservableCollection<HighlightRule>(
            (view?.Rules ?? []).Select(r => r.Clone()));

        GlobalGrid.ItemsSource = _global;
        ViewGrid.ItemsSource = _view;

        ViewTab.Header = view is null ? "View rules" : $"View rules ({view.Name})";
        ViewTab.IsEnabled = view is not null;

        TestBox.Text = "2026-08-14 09:12:44.117 ERROR OrderService Timeout calling payments";
        Loaded += (_, __) => RunTest();
    }

    public IReadOnlyList<HighlightRule> ResultGlobalRules => _global;
    public IReadOnlyList<HighlightRule> ResultViewRules => _view;

    private DataGrid ActiveGrid => Scope.SelectedIndex == 1 ? ViewGrid : GlobalGrid;
    private ObservableCollection<HighlightRule> ActiveList => Scope.SelectedIndex == 1 ? _view : _global;

    // ---- list editing -------------------------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new HighlightRule { Name = "New rule", Pattern = "", Foreground = "#E6E6E6" };
        ActiveList.Add(rule);
        ActiveGrid.SelectedItem = rule;
        ActiveGrid.ScrollIntoView(rule);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveGrid.SelectedItem is not HighlightRule r) return;
        var copy = r.Clone();
        copy.Name = r.Name + " copy";
        ActiveList.Insert(ActiveList.IndexOf(r) + 1, copy);
        ActiveGrid.SelectedItem = copy;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveGrid.SelectedItem is HighlightRule r) ActiveList.Remove(r);
        RunTest();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        var grid = ActiveGrid;
        var list = ActiveList;
        int i = grid.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;

        list.Move(i, j);
        grid.SelectedIndex = j;
        grid.ScrollIntoView(list[j]);
        RunTest();
    }

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        foreach (var preset in RulePresets.All)
        {
            var item = new MenuItem
            {
                Header = preset.Name,
                ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = preset.Description,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 380
                    }
                }
            };

            var captured = preset;
            item.Click += (_, __) => ApplyPreset(captured);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ApplyPreset(RulePreset preset)
    {
        var scope = Scope.SelectedIndex == 1 ? "this view's rules" : "the global rules";

        if (MessageBox.Show(
                $"Replace {scope} with \"{preset.Name}\"?\n\n{preset.Description}",
                "LogLens", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var list = ActiveList;
        list.Clear();
        foreach (var r in preset.Build()) list.Add(r);
        RunTest();
    }

    // ---- colours ------------------------------------------------------------------

    private void PickForeground_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HighlightRule r) return;
        if (PickColour(r.Foreground, out var hex)) r.Foreground = hex;
        RunTest();
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HighlightRule r) return;

        // Shift+click clears the background back to "no fill".
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            r.Background = "";
            RunTest();
            return;
        }

        if (PickColour(r.Background, out var hex)) r.Background = hex;
        RunTest();
    }

    private static bool PickColour(string current, out string hex)
    {
        hex = "";
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };

        try
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                var c = (Color)ColorConverter.ConvertFromString(current)!;
                dlg.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
        }
        catch { /* fall back to the dialog default */ }

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

        hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        return true;
    }

    // ---- tester --------------------------------------------------------------------

    private void TestBox_TextChanged(object sender, TextChangedEventArgs e) => RunTest();

    private void RunTest()
    {
        if (TestResult is null) return;

        // Commit whatever cell is mid-edit so the tester reflects what you just typed.
        GlobalGrid.CommitEdit();
        ViewGrid.CommitEdit();

        var set = new RuleSet(_view, _global);
        var match = set.Match(TestBox.Text);

        if (match is null)
        {
            TestResult.Text = "no rule matches";
            TestResult.Foreground = (Brush)FindResource("App.FgDim");
            TestResult.Background = Brushes.Transparent;
            TestResult.FontWeight = FontWeights.Normal;
            return;
        }

        TestResult.Text = $"{match.Name}  ({match.Severity})";
        TestResult.Foreground = Core.CachedHexBrushConverter.Resolve(match.Foreground) ?? (Brush)FindResource("App.Fg");
        TestResult.Background = Core.CachedHexBrushConverter.Resolve(match.Background) ?? Brushes.Transparent;
        TestResult.FontWeight = match.Bold ? FontWeights.Bold : FontWeights.Normal;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        GlobalGrid.CommitEdit();
        ViewGrid.CommitEdit();

        var bad = _global.Concat(_view)
            .Where(r => r.Enabled && !string.IsNullOrEmpty(r.Pattern))
            .FirstOrDefault(r => { r.Matches("probe"); return r.CompileError is not null; });

        if (bad is not null)
        {
            var answer = MessageBox.Show(
                $"Rule '{bad.Name}' has an invalid pattern:\n\n{bad.CompileError}\n\n" +
                "It will simply never match. Save anyway?",
                "LogLens", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.No) return;
        }

        DialogResult = true;
    }
}
