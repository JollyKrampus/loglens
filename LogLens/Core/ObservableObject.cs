using System.Windows.Input;

namespace LogLens.Core;

// ObservableObject itself lives in LogLens.Core (the cross-platform library);
// only this WPF-specific command helper stays in the app.

/// <summary>Minimal ICommand so we don't drag in an MVVM framework.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null)
    {
        _exec = exec;
        _can = can;
    }

    public RelayCommand(Action exec, Func<bool>? can = null)
        : this(_ => exec(), can is null ? null : _ => can()) { }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _exec(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
