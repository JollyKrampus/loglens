using System.Windows;

namespace LogLens.Core;

/// <summary>WPF's side of the dispatcher abstraction the view-models depend on.</summary>
public sealed class WpfUiThread : IUiThread
{
    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
