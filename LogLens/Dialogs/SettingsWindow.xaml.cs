using System.Windows;
using LogLens.Models;

namespace LogLens.Dialogs;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = settings;   // edits apply live; there's nothing here worth a Cancel
    }

    private void PickFont_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FontDialog
        {
            FixedPitchOnly = true,
            ShowEffects = false
        };

        try
        {
            var first = _settings.FontFamily.Split(',')[0].Trim();
            dlg.Font = new System.Drawing.Font(first, (float)_settings.FontSize);
        }
        catch { /* the dialog default is fine */ }

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _settings.FontFamily = dlg.Font.Name;
        _settings.FontSize = dlg.Font.Size;
        FontBox.Text = dlg.Font.Name;
    }
}
