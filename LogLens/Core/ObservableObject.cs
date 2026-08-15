using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LogLens.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

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
