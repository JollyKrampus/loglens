using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LogLens.Models;

namespace LogLens.Dialogs;

public partial class ViewEditWindow : Window
{
    private readonly ViewDef _def;
    private readonly ObservableCollection<LogSource> _sources;

    public ViewEditWindow(ViewDef def)
    {
        InitializeComponent();
        _def = def;

        NameBox.Text = def.Name;
        AccentBox.Text = def.Accent;

        // Edit a copy so Cancel really cancels.
        _sources = new ObservableCollection<LogSource>(def.Sources.Select(s => s.Clone()));
        SourceList.ItemsSource = _sources;

        Loaded += (_, __) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private LogSource? Selected => SourceList.SelectedItem as LogSource;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add log files",
            Multiselect = true,
            Filter = "Log files (*.log;*.txt;*.out;*.err)|*.log;*.txt;*.out;*.err|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames) _sources.Add(new LogSource { Path = f });
    }

    private void AddWildcard_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PromptWindow(
            "Add wildcard path",
            "Path with * or ? — the newest matching file is tailed.\n" +
            @"Example:  \\prod-app01\logs\service-*.log",
            "") { Owner = this };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        _sources.Add(new LogSource { Path = dlg.Value.Trim() });
    }

    private void EditSource_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;

        var dlg = new PromptWindow("Edit path", "Full path to the log file (wildcards allowed):", s.Path)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        s.Path = dlg.Value.Trim();
        SourceList.Items.Refresh();
    }

    private void RenameSource_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;

        var dlg = new PromptWindow("Rename tab", "Display name (blank uses the file name):", s.Name)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        s.Name = dlg.Value.Trim();
        SourceList.Items.Refresh();
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) _sources.Remove(s);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int i = SourceList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _sources.Count) return;
        _sources.Move(i, j);
        SourceList.SelectedIndex = j;
    }

    private void PickAccent_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };

        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(AccentBox.Text)!;
            dlg.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch { /* keep the dialog default */ }

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        AccentBox.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("Give the view a name.", "LogLens",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        _def.Name = name;
        _def.Accent = string.IsNullOrWhiteSpace(AccentBox.Text) ? "#4C8DFF" : AccentBox.Text.Trim();

        _def.Sources.Clear();
        _def.Sources.AddRange(_sources);

        DialogResult = true;
    }
}
