// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>The base of every view model: <see cref="Set{T}"/> a field and the binding hears it.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores <paramref name="value"/> and raises the change; false when it was already that.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises the change of one property; with null or "", of all of them.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A command from a delegate. <c>new RelayCommand(() =&gt; …)</c> or with a parameter.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = _ => run();
        _can = can is null ? null : _ => can();
    }

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _can = can;
    }

    /// <summary>An async action; a fault goes to the app's log and toast instead of the dispatcher.</summary>
    public static RelayCommand Async(Func<Task> run, Func<bool>? can = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RelayCommand(() => _ = Guard(run()), can);
    }

    private static async Task Guard(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A cancelled action is not a fault.
        }
#pragma warning disable CA1031 // a failed action is reported, it never takes the shell down
        catch (Exception e)
#pragma warning restore CA1031
        {
            App.Report(e);
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _run(parameter);
}
