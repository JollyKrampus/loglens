using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LogLens.Models;
using LogLens.Services;
using LogLens.ViewModels;

namespace LogLens.Controls;

public partial class LogPane : UserControl
{
    private ScrollViewer? _scroller;
    private LogPaneVm? _tab;
    private bool _scrollPending;
    private List<int> _hits = new();
    private string _hitsTerm = "";

    public LogPane()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, __) => _scroller = FindScroller();

        Lines.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));

        // Resizing the window changes the viewport; stay pinned to the tail if following.
        Lines.SizeChanged += (_, __) =>
        {
            if (_tab?.FollowTail == true) ScheduleScrollToEnd();
        };

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, __) => CopySelected(true)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, __) => ShowFind()));
    }

    // ---- dependency properties fed from AppSettings ------------------------------

    public static readonly DependencyProperty WordWrapProperty =
        DependencyProperty.Register(nameof(WordWrap), typeof(bool), typeof(LogPane),
            new PropertyMetadata(false));

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public static readonly DependencyProperty ShowLineNumbersProperty =
        DependencyProperty.Register(nameof(ShowLineNumbers), typeof(bool), typeof(LogPane),
            new PropertyMetadata(true));

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public static readonly DependencyProperty LogFontFamilyProperty =
        DependencyProperty.Register(nameof(LogFontFamily), typeof(FontFamily), typeof(LogPane),
            new PropertyMetadata(new FontFamily("Consolas")));

    public FontFamily LogFontFamily
    {
        get => (FontFamily)GetValue(LogFontFamilyProperty);
        set => SetValue(LogFontFamilyProperty, value);
    }

    public static readonly DependencyProperty LogFontSizeProperty =
        DependencyProperty.Register(nameof(LogFontSize), typeof(double), typeof(LogPane),
            new PropertyMetadata(12.5));

    public double LogFontSize
    {
        get => (double)GetValue(LogFontSizeProperty);
        set => SetValue(LogFontSizeProperty, value);
    }

    // ---- wiring ------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null) _tab.RequestScrollToEnd = null;

        _tab = DataContext as LogPaneVm;
        _hits.Clear();
        _hitsTerm = "";

        if (_tab is null) return;

        _tab.RequestScrollToEnd = ScheduleScrollToEnd;
        _tab.RevealIndexRequested = i =>
        {
            if (i < 0 || i >= Lines.Items.Count) return;
            Lines.SelectedIndex = i;
            Lines.ScrollIntoView(Lines.SelectedItem);
        };
        if (_tab.FollowTail) ScheduleScrollToEnd();
    }

    private ScrollViewer? FindScroller()
    {
        if (VisualTreeHelper.GetChildrenCount(Lines) == 0) return null;
        var border = VisualTreeHelper.GetChild(Lines, 0);
        return VisualTreeHelper.GetChildrenCount(border) > 0
            ? VisualTreeHelper.GetChild(border, 0) as ScrollViewer
            : null;
    }

    /// <summary>
    /// Coalesces scroll requests. A busy log can produce many batches per second and
    /// we only ever need to land at the bottom once per frame.
    /// </summary>
    private void ScheduleScrollToEnd()
    {
        if (_scrollPending) return;
        _scrollPending = true;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            ScrollToEndCore();
        });
    }

    /// <summary>
    /// ScrollViewer.ScrollToEnd alone undershoots here: with virtualization the extent
    /// is an estimate built from realised containers, so "the end" moves as rows realise.
    /// ScrollIntoView forces the last item to materialise, then ScrollToEnd settles any
    /// residual gap once the extent is accurate.
    /// </summary>
    private void ScrollToEndCore()
    {
        // Re-check FollowTail at run time, not just at queue time: a go-to-source
        // reveal can turn it off between the two, and without this check the queued
        // scroll dragged the view back to the tail right after the reveal landed.
        if (_tab is null || !_tab.FollowTail) return;

        var items = _tab.Display;
        if (items.Count == 0) return;

        Lines.ScrollIntoView(items[^1]);

        _scroller ??= FindScroller();
        _scroller?.ScrollToEnd();
    }

    /// <summary>
    /// Scrolling up drops out of follow mode, scrolling back to the bottom re-arms it.
    /// ExtentHeightChange separates "the user moved" from "the content grew".
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_tab is null) return;
        if (e.ExtentHeightChange != 0) return;      // content grew, not a user gesture
        if (e.VerticalChange == 0) return;

        bool atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
        if (_tab.FollowTail != atBottom) _tab.FollowTail = atBottom;
    }

    // ---- toolbar -----------------------------------------------------------------

    private void Clear_Click(object sender, RoutedEventArgs e) => _tab?.ClearBuffer();

    private void Reload_Click(object sender, RoutedEventArgs e) => _tab?.ReloadFromDisk();

    // Resolve + launch run off the UI thread: both touch the file system, and for a
    // tab tailing an unreachable UNC share that means blocking for the full SMB
    // connect timeout — long enough to take the whole window to "not responding".

    /// <summary>
    /// The file these actions target: the tab's own file, or — on the merged
    /// timeline, which has no single file — the SELECTED LINE's source file.
    /// </summary>
    private LogTab? ResolveTargetTab(out string? whyNot)
    {
        whyNot = null;

        if (_tab is LogTab file) return file;

        if (_tab is MergedTab merged)
        {
            if (Lines.SelectedItem is not LogLine line)
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

    private async void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        var tab = ResolveTargetTab(out var whyNot);
        if (tab is null)
        {
            if (whyNot is not null)
                MessageBox.Show(whyNot, "LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var error = await Task.Run(() => ShellOpen.OpenInEditor(tab.ResolvedFilePath));
        if (error is not null)
            MessageBox.Show(error, "LogLens", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void RevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var tab = ResolveTargetTab(out var whyNot);
        if (tab is null)
        {
            if (whyNot is not null)
                MessageBox.Show(whyNot, "LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var error = await Task.Run(() => ShellOpen.RevealInExplorer(tab.ResolvedFilePath));
        if (error is not null)
            MessageBox.Show(error, "LogLens", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void GoToSourceTab_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is not MergedTab merged) return;

        if (Lines.SelectedItem is not LogLine line)
        {
            MessageBox.Show("Select a line first.", "LogLens",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = merged.SourceTabFor(line);
        if (source is null)
        {
            MessageBox.Show("That line's file tab is no longer in this view.", "LogLens",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        merged.NavigateToSourceRequested?.Invoke(source, line);
    }

    private void ShowFind_Click(object sender, RoutedEventArgs e) => ShowFind();

    private void HideFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        Lines.Focus();
    }

    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    // ---- find --------------------------------------------------------------------

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHits();

    private void FindOptionChanged(object sender, RoutedEventArgs e) => RefreshHits();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FindStep(-1);
        else FindStep(+1);
        e.Handled = true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindStep(-1);

    private string HitKey =>
        $"{FindBox.Text}{FindRegex.IsChecked == true}{FindCase.IsChecked == true}{_tab?.ShownLines}";

    private void RefreshHits()
    {
        _hits = new List<int>();
        _hitsTerm = HitKey;

        var term = FindBox.Text;
        if (_tab is null || string.IsNullOrEmpty(term))
        {
            FindStatus.Text = "";
            return;
        }

        Regex? rx = null;
        if (FindRegex.IsChecked == true)
        {
            try
            {
                var opts = RegexOptions.CultureInvariant;
                if (FindCase.IsChecked != true) opts |= RegexOptions.IgnoreCase;
                rx = new Regex(term, opts, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                FindStatus.Text = ex.Message;
                return;
            }
        }

        var cmp = FindCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var items = _tab.Display;
        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            bool hit = rx is not null ? rx.IsMatch(text) : text.Contains(term, cmp);
            if (hit) _hits.Add(i);
        }

        FindStatus.Text = _hits.Count == 0 ? "no matches" : $"{_hits.Count:N0} matches";
    }

    private void FindStep(int direction)
    {
        if (_tab is null) return;
        if (_hitsTerm != HitKey) RefreshHits();
        if (_hits.Count == 0) return;

        int current = Lines.SelectedIndex;
        int target;

        if (direction > 0)
        {
            target = _hits.FirstOrDefault(i => i > current, -1);
            if (target < 0) target = _hits[0];              // wrap to the top
        }
        else
        {
            target = _hits.LastOrDefault(i => i < current, -1);
            if (target < 0) target = _hits[^1];             // wrap to the bottom
        }

        // Jumping to a hit means you want to read it, not be dragged back to the tail.
        _tab.FollowTail = false;

        Lines.SelectedIndex = target;
        Lines.ScrollIntoView(Lines.SelectedItem);

        int ordinal = _hits.IndexOf(target) + 1;
        FindStatus.Text = $"{ordinal:N0} of {_hits.Count:N0}";
    }

    // ---- context menu ------------------------------------------------------------

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected(true);
    private void CopyRaw_Click(object sender, RoutedEventArgs e) => CopySelected(false);

    private void CopySelected(bool withNumbers)
    {
        if (Lines.SelectedItems.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in Lines.SelectedItems)
        {
            if (item is not LogLine l) continue;
            if (withNumbers && ShowLineNumbers) sb.Append(l.Number).Append('\t');
            sb.AppendLine(l.Text);
        }

        try { Clipboard.SetText(sb.ToString()); }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; not worth crashing over.
            FindStatus.Text = "Copy failed: " + ex.Message;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Lines.SelectAll();

    private void FilterToSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null || Lines.SelectedItem is not LogLine l) return;
        _tab.Filter.IsRegex = false;
        _tab.Filter.Include = l.Text.Trim();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
        => _tab?.ClearAllFilters();
}
