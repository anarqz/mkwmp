// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;

namespace Mkwmp.App.Views;

/// <summary>
/// What a place that needs a server shows while there is none: a lock, <see cref="Title"/>,
/// <see cref="Text"/> and the "Configure online" button (the DataContext's <c>ConfigureOnlineCommand</c>).
/// </summary>
public partial class OnlineLocked : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    public static readonly DependencyProperty ShownTitleProperty =
        DependencyProperty.Register(nameof(ShownTitle), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    public static readonly DependencyProperty ShownTextProperty =
        DependencyProperty.Register(nameof(ShownText), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    public static readonly DependencyProperty ButtonTextProperty =
        DependencyProperty.Register(nameof(ButtonText), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    private ViewModels.ShellViewModel? _shell;

    public OnlineLocked()
    {
        InitializeComponent();
        Loaded += (_, _) => Follow(Application.Current?.MainWindow?.DataContext as ViewModels.ShellViewModel);
        Unloaded += (_, _) => Follow(null);
    }

    /// <summary>What is shown: <see cref="Title"/>, or, in a run that is offline only until MKWMP is updated, that one reason (the same in every locked place).</summary>
    public string ShownTitle => (string)GetValue(ShownTitleProperty);

    public string ShownText => (string)GetValue(ShownTextProperty);

    /// <summary>"Configure Online", or "Try the Update Again".</summary>
    public string ButtonText => (string)GetValue(ButtonTextProperty);

    private void Follow(ViewModels.ShellViewModel? shell)
    {
        if (_shell is not null)
        {
            _shell.StateChanged -= OnShellChanged;
        }
        _shell = shell;
        if (shell is not null)
        {
            shell.StateChanged += OnShellChanged;
        }
        Show();
    }

    private void OnShellChanged(object? sender, EventArgs e) => Show();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TitleProperty || e.Property == TextProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        var update = _shell is { OnlineBlockedByUpdate: true };
        // PLAY goes to Nextendo Network: what is locked here is an MKWMP server's, and the way back is the same Settings › Online
        var nextendo = !update && _shell is { PlaysOnNextendo: true };
        SetValue(ShownTitleProperty, update ? _shell!.S["core.update_to_play_online"] : nextendo ? _shell!.S["nextendo.locked.title"] : Title);
        SetValue(ShownTextProperty, update ? _shell!.S["online.update.text"] : nextendo ? _shell!.S["nextendo.locked.text"] : Text);
        SetValue(ButtonTextProperty, _shell?.OnlineLockLabel ?? "");
    }

    /// <summary>What lives on a server ("Friends live on an MKWMP server").</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One or two lines under it.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
