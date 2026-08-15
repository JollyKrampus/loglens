using System.Windows;
using LogLens.Models;
using LogLens.Services;

namespace LogLens.Dialogs;

public partial class AlertsWindow : Window
{
    private readonly AlertService _service;
    private readonly Window _shell;

    public AlertsWindow(AlertSettings settings, AlertService service, Window shell)
    {
        InitializeComponent();
        _service = service;
        _shell = shell;
        DataContext = settings;   // edits apply live; nothing here warrants a Cancel
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        // Fire against the main window so the taskbar flash lands on the right button.
        _service.SendTestAlert(_shell);
    }
}
