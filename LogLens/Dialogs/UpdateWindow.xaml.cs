using System.Diagnostics;
using System.Windows;
using LogLens.Services;

namespace LogLens.Dialogs;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private bool _busy;

    public UpdateWindow(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        VersionLine.Text =
            $"You are on {update.Current}.  Version {update.Latest} is on the releases page "
            + $"({update.ExeSizeBytes / (1024 * 1024)} MB download).";
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_update.ReleaseUrl) { UseShellExecute = true }); }
        catch { /* browser trouble is not worth a dialog */ }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;

        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusLine.Visibility = Visibility.Visible;
        StatusLine.Text = "Downloading…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusLine.Text = $"Downloading… {p:P0}";
            });

            var staged = await UpdateService.DownloadAndVerifyAsync(_update, progress);

            StatusLine.Text = "Verified. Restarting…";

            // Swap and start the new exe, then take the whole app down. The main
            // window's Closing handler saves the workspace on the way out.
            UpdateService.ApplyAndRestart(staged);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Update failed: " + ex.Message;
            Progress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            _busy = false;
        }
    }
}
