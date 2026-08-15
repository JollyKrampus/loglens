using System.Windows;
using System.Windows.Input;

namespace LogLens.Dialogs;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string message, string initial)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Input.Text = initial;
        Loaded += (_, __) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
