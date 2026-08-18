using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LogLens;

public partial class App : Application
{
    private static bool _showingError;
    private static int _suppressed;

    protected override void OnStartup(StartupEventArgs e)
    {
        // After a self-update the predecessor is still saving its workspace and
        // checkpointing the issue database as we start; wait it out before touching
        // either. No-op on a normal launch.
        Services.UpdateService.WaitForPredecessor(e.Args);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain");

        base.OnStartup(e);
    }

    /// <summary>
    /// A crash in a background tail must not take the app down. The re-entrancy guard
    /// matters: MessageBox.Show pumps the dispatcher, so a layout exception that keeps
    /// re-throwing would otherwise open dialogs until the stack blows.
    /// </summary>
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogCrash(e.Exception, "Dispatcher");

        if (_showingError) { _suppressed++; return; }

        _showingError = true;
        try
        {
            MessageBox.Show(
                $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
                $"Details were written to:\n{CrashLogPath}",
                "LogLens", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            _suppressed = 0;
        }
    }

    public static string CrashLogPath =>
        Path.Combine(Path.GetTempPath(), "loglens-errors.log");

    private static void LogCrash(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(CrashLogPath,
                $"=== {DateTime.Now:O} [{source}] ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never itself throw */ }
    }

    /// <summary>Swaps the theme dictionary in slot 0.</summary>
    public static void ApplyTheme(bool light)
    {
        var uri = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }
}
