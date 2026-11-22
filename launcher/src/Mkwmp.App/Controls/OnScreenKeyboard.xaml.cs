// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Controls;

/// <summary>
/// The on-screen keyboard: a sheet of keys that rises over everything while <c>Shell.Keyboard</c>
/// is set. The D-pad moves over the keys with the focus card like anywhere else; X backspace,
/// Y space, LB shift, Start OK, B cancel. See <see cref="TextEntry"/> for how a text box gets it.
/// </summary>
public partial class OnScreenKeyboard : UserControl
{
    private ShellViewModel? _shell;

    static OnScreenKeyboard() => TextEntry.RegisterClassHandlers();

    public OnScreenKeyboard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ShellViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as ShellViewModel);
    }

    private void Attach(ShellViewModel? shell)
    {
        if (ReferenceEquals(shell, _shell))
        {
            return;
        }
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellChanged;
        }
        _shell = shell;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellChanged;
            Show(_shell.Keyboard);
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Keyboard))
        {
            Show(_shell?.Keyboard);
        }
    }

    /// <summary>The panel keeps the last keyboard while it slides away, so it does not go blank on the way out.</summary>
    private void Show(KeyboardViewModel? keyboard)
    {
        if (keyboard is not null)
        {
            Sheet.DataContext = keyboard;
        }
    }

    private void OnScrim(object sender, MouseButtonEventArgs e)
    {
        if (Input.Tap.IsTap((System.Windows.DependencyObject)sender))
        {
            _shell?.Keyboard?.Cancel();
        }
    }
}
