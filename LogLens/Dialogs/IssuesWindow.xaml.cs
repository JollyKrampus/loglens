using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Dialogs;

public partial class IssuesWindow : Window
{
    private readonly IssueRecorder _recorder;
    private readonly AppSettings _settings;
    private List<LogIssue> _rows = [];
    private bool _loading = true;

    public IssuesWindow(IssueRecorder recorder, AppSettings settings)
    {
        InitializeComponent();
        _recorder = recorder;
        _settings = settings;

        FatalFilter.IsChecked = true;
        ErrorFilter.IsChecked = true;
        WarnFilter.IsChecked = false;   // warnings are usually noise until you go looking
        HideFiled.IsChecked = false;
        ShowIgnored.IsChecked = false;

        _loading = false;
        Reload();
    }

    private LogIssue? Selected => Grid.SelectedItem as LogIssue;

    // ---- loading -------------------------------------------------------------------

    private void Reload()
    {
        if (_loading) return;

        // Make sure anything still queued is on disk before we read.
        _recorder.Flush();

        var counts = _recorder.Store.CountsBySeverity(ShowIgnored.IsChecked == true);
        FatalFilter.Content = $"Fatal ({counts.GetValueOrDefault(Severity.Fatal)})";
        ErrorFilter.Content = $"Error ({counts.GetValueOrDefault(Severity.Error)})";
        WarnFilter.Content = $"Warn ({counts.GetValueOrDefault(Severity.Warn)})";

        var wanted = new List<Severity>();
        if (FatalFilter.IsChecked == true) wanted.Add(Severity.Fatal);
        if (ErrorFilter.IsChecked == true) wanted.Add(Severity.Error);
        if (WarnFilter.IsChecked == true) wanted.Add(Severity.Warn);

        var search = SearchBox.Text;
        var results = new List<LogIssue>();

        foreach (var sev in wanted)
        {
            results.AddRange(_recorder.Store.Query(
                severity: sev,
                includeIgnored: ShowIgnored.IsChecked == true,
                includeFiled: HideFiled.IsChecked != true,
                search: search));
        }

        _rows = results
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.Count)
            .ThenByDescending(i => i.LastSeenUtc)
            .ToList();

        var keepHash = Selected?.Hash;
        Grid.ItemsSource = _rows;

        if (keepHash is not null)
            Grid.SelectedItem = _rows.FirstOrDefault(i => i.Hash == keepHash);

        long total = _rows.Sum(i => i.Count);
        StatusText.Text = _rows.Count == 0
            ? "No issues recorded yet. They accumulate as fatal, error and warning lines arrive."
            : $"{_rows.Count:N0} distinct issue(s) · {total:N0} total occurrence(s) · {_recorder.Store.DatabasePath}";

        ShowDetail(Selected);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Filter_Changed(object sender, RoutedEventArgs e) => Reload();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Reload();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(Selected);

    private void ShowDetail(LogIssue? issue)
    {
        if (issue is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = issue.Title;

        var facts = new StringBuilder();
        facts.Append($"{issue.Severity} · seen {issue.Count:N0} time(s)\n");
        facts.Append($"First {issue.FirstSeenLocal:yyyy-MM-dd HH:mm:ss}   Last {issue.LastSeenLocal:yyyy-MM-dd HH:mm:ss}\n");
        if (!string.IsNullOrWhiteSpace(issue.Views)) facts.Append($"Environments: {issue.Views}\n");
        if (!string.IsNullOrWhiteSpace(issue.Sources)) facts.Append($"Files: {issue.Sources}\n");
        if (!string.IsNullOrWhiteSpace(issue.ExceptionType)) facts.Append($"Exception: {issue.ExceptionType}\n");
        if (!string.IsNullOrWhiteSpace(issue.FaultingMethod)) facts.Append($"Method: {issue.FaultingMethod}\n");
        DetailFacts.Text = facts.ToString().TrimEnd();

        var sample = new StringBuilder();
        sample.AppendLine(issue.SampleLine);
        if (!string.IsNullOrWhiteSpace(issue.SampleDetail)) sample.AppendLine(issue.SampleDetail);
        sample.AppendLine();
        sample.AppendLine("--- grouping signature ---");
        sample.AppendLine(issue.Signature);
        DetailSample.Text = sample.ToString();

        JiraKeyBox.Text = issue.JiraKey ?? "";
        IgnoreButton.Content = issue.Ignored ? "Un-ignore" : "Ignore";
    }

    // ---- actions -------------------------------------------------------------------

    private void CopyJira_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;
        CopyToClipboard(JiraTemplate.Full(issue, _settings.JiraProjectKey), "Jira ticket copied");
    }

    private void CopyPlain_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;
        CopyToClipboard(JiraTemplate.PlainText(issue), "Copied as plain text");
    }

    private void CopyToClipboard(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = done;
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open.
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    private void CreateInJira_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;

        var url = JiraTemplate.CreateUrl(issue, _settings.JiraBaseUrl, _settings.JiraProjectKey);
        if (url is null)
        {
            MessageBox.Show(
                "Set your Jira URL in Tools ▸ Settings first, for example:\n\n" +
                "    https://yourcompany.atlassian.net",
                "LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The description is too long for a URL, so put it on the clipboard ready to paste.
        CopyToClipboard(JiraTemplate.Description(issue), "Description copied — paste it into the Jira form");
        OpenUrl(url);
    }

    private void OpenJira_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;

        var url = JiraTemplate.BrowseUrl(_settings.JiraBaseUrl, JiraKeyBox.Text);
        if (url is null)
        {
            MessageBox.Show("Record a Jira key, and set your Jira URL in Tools ▸ Settings.",
                "LogLens", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the browser:\n\n" + ex.Message,
                "LogLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void JiraKey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;

        var key = JiraKeyBox.Text.Trim();
        if (key == (issue.JiraKey ?? "")) return;

        _recorder.Store.SetJiraKey(issue.Hash, key.Length == 0 ? null : key);
        issue.JiraKey = key.Length == 0 ? null : key;
        Reload();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;
        _recorder.Store.SetIgnored(issue.Hash, !issue.Ignored);
        Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } issue) return;

        if (MessageBox.Show(
                $"Delete this issue and its history?\n\n{issue.Title}\n\n" +
                "It will reappear if the same problem is logged again.",
                "LogLens", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _recorder.Store.Delete(issue.Hash);
        Reload();
    }

    private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_recorder.Store.DatabasePath);
            if (dir is not null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* not worth an error dialog */ }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export issues",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"loglens-issues-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity,Count,FirstSeen,LastSeen,Title,Exception,Method,Environments,Files,JiraKey");

            foreach (var i in _rows)
            {
                sb.AppendLine(string.Join(",",
                    Csv(i.Severity.ToString()), Csv(i.Count.ToString()),
                    Csv(i.FirstSeenLocal.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(i.LastSeenLocal.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(i.Title), Csv(i.ExceptionType), Csv(i.FaultingMethod),
                    Csv(i.Views), Csv(i.Sources), Csv(i.JiraKey)));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusText.Text = "Exported " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LogLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Csv(string? value)
    {
        var s = value ?? "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}
