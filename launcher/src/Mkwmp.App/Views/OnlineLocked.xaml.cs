// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;

namespace Mkwmp.App.Views;

/// <summary>
/// What a place that needs an MKWMP server shows while there is none: a lock, <see cref="Title"/>,
/// <see cref="Text"/> and the "Configure online" button (the DataContext's <c>ConfigureOnlineCommand</c>).
/// While Nextendo Network is where PLAY goes online it says that this place is an MKWMP server's, and its button is
/// "Sign in with Nextendo" until someone is signed in (then there is none): it never sends anyone to an MKWMP server.
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

    public static readonly DependencyProperty NextendoTitleProperty =
        DependencyProperty.Register(nameof(NextendoTitle), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

    public static readonly DependencyProperty NextendoTextProperty =
        DependencyProperty.Register(nameof(NextendoText), typeof(string), typeof(OnlineLocked), new PropertyMetadata(""));

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
        if (e.Property == TitleProperty || e.Property == TextProperty || e.Property == NextendoTitleProperty || e.Property == NextendoTextProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        var update = _shell is { OnlineBlockedByUpdate: true };
        // Nextendo Network is where PLAY goes online: what is locked here is an MKWMP server's, and it says so without
        // sending anyone to one.
        var nextendo = !update && _shell is { NextendoFirst: true };
        // A place that Nextendo has too (friends) says so in its own words: what signing in gives.
        var own = nextendo && NextendoTitle.Length > 0;
        SetValue(ShownTitleProperty, update ? _shell!.S["core.update_to_play_online"] : own ? NextendoTitle : nextendo ? _shell!.S["nextendo.locked.title"] : Title);
        SetValue(ShownTextProperty, update ? _shell!.S["online.update.text"] : own ? NextendoText : nextendo ? _shell!.S["nextendo.locked.text"] : Text);
        // signed in on Nextendo there is nothing to do here: no button (MKWMP servers are Settings › Online's alternative, as the text says)
        SetValue(ButtonTextProperty, nextendo && _shell is { NextendoNeedsSignIn: false } ? "" : _shell?.OnlineLockLabel ?? "");
    }

    /// <summary>What lives on a server ("Friends live on an MKWMP server").</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// What it says instead while Nextendo Network is where PLAY goes online, for a place Nextendo has too (the
    /// friends panel: "Your Nextendo friends"); "" for a place that is only an MKWMP server's (voice, camera).
    /// </summary>
    public string NextendoTitle
    {
        get => (string)GetValue(NextendoTitleProperty);
        set => SetValue(NextendoTitleProperty, value);
    }

    public string NextendoText
    {
        get => (string)GetValue(NextendoTextProperty);
        set => SetValue(NextendoTextProperty, value);
    }

    /// <summary>One or two lines under it.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
