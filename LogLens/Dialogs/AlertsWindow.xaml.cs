using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Dialogs;

public partial class AlertsWindow : Window
{
    private readonly AlertSettings _settings;
    private readonly AlertService _service;
    private readonly Window _shell;

    private readonly ObservableCollection<AlertSound> _sounds = [];
    private readonly ObservableCollection<AlertSound> _fatalSounds = [];

    /// <summary>Suppresses the preview while the dialog is populating the combos.</summary>
    private bool _loading = true;

    public AlertsWindow(AlertSettings settings, AlertService service, Window shell)
    {
        InitializeComponent();
        _settings = settings;
        _service = service;
        _shell = shell;
        DataContext = settings;   // edits apply live; nothing here warrants a Cancel

        Fill(_sounds, settings.SoundName);
        Fill(_fatalSounds, settings.FatalSoundName);

        BindGrouped(SoundCombo, _sounds);
        BindGrouped(FatalSoundCombo, _fatalSounds);

        Loaded += (_, __) => _loading = false;
    }

    /// <summary>
    /// Seeds a list with the library, plus the currently selected sound if it is a
    /// custom file that isn't in it — otherwise the combo would show blank.
    /// </summary>
    private static void Fill(ObservableCollection<AlertSound> target, string? selectedId)
    {
        target.Clear();
        foreach (var s in SoundLibrary.All) target.Add(s);

        if (string.IsNullOrWhiteSpace(selectedId)) return;
        if (target.Any(s => string.Equals(s.Id, selectedId, StringComparison.OrdinalIgnoreCase))) return;

        target.Insert(0, SoundLibrary.ForCustomFile(selectedId));
    }

    /// <summary>Groups the dropdown into System / Recommended / All, which makes 70-odd entries navigable.</summary>
    private static void BindGrouped(ComboBox combo, ObservableCollection<AlertSound> source)
    {
        var view = new CollectionViewSource { Source = source };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AlertSound.Group)));
        combo.ItemsSource = view.View;

        combo.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = BuildGroupHeaderTemplate()
        });
    }

    private static DataTemplate BuildGroupHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.OpacityProperty, 0.75);
        text.SetValue(TextBlock.MarginProperty, new Thickness(6, 6, 6, 2));

        var template = new DataTemplate { VisualTree = text };
        template.Seal();
        return template;
    }

    // ---- previews ------------------------------------------------------------------

    private void Sound_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SoundLibrary.Play(_settings.SoundName);
    }

    private void FatalSound_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SoundLibrary.Play(_settings.FatalSoundName);
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
        => SoundLibrary.Play(_settings.SoundName);

    private void PreviewFatalSound_Click(object sender, RoutedEventArgs e)
        => SoundLibrary.Play(_settings.FatalSoundName);

    // ---- custom files --------------------------------------------------------------

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
        => Browse(_sounds, SoundCombo, id => _settings.SoundName = id);

    private void BrowseFatalSound_Click(object sender, RoutedEventArgs e)
        => Browse(_fatalSounds, FatalSoundCombo, id => _settings.FatalSoundName = id);

    private void Browse(ObservableCollection<AlertSound> list, ComboBox combo, Action<string> assign)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an alert sound",
            Filter = "Wave audio (*.wav)|*.wav|All files (*.*)|*.*",
            InitialDirectory = SoundLibrary.MediaFolder
        };
        if (dlg.ShowDialog(this) != true) return;

        var existing = list.FirstOrDefault(s =>
            string.Equals(s.Id, dlg.FileName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = SoundLibrary.ForCustomFile(dlg.FileName);
            list.Insert(0, existing);
        }

        assign(existing.Id);
        combo.SelectedValue = existing.Id;
        SoundLibrary.Play(existing.Id);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        // Fire against the main window so the taskbar flash lands on the right button.
        _service.SendTestAlert(_shell);
    }
}
