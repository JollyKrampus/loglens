namespace LogLens.Core;

/// <summary>
/// The one thing the view-models need from a UI framework: "run this on the UI
/// thread, later". WPF implements it with its Dispatcher, Avalonia with
/// Dispatcher.UIThread — and the view-models stay platform-free.
/// </summary>
public interface IUiThread
{
    void Post(Action action);
}
