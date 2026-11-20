// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Controls;

/// <summary>The top of the home page: avatars, clock, server connection, battery.</summary>
public partial class TopBar : UserControl
{
    public TopBar() => InitializeComponent();

    private void OnAvatar(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            // on that friend's line; the player's own disc opens the panel at its top (or on the first request)
            shell.OpenFriends((sender as FrameworkElement)?.DataContext is TopAvatar { IsSelf: false } friend ? friend.Name : null);
        }
    }
}

/// <summary>The bottom line: which input is in use at the left, what the buttons do here at the right.</summary>
public partial class HintBar : UserControl
{
    public HintBar() => InitializeComponent();

    private void OnHint(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Hint hint } && DataContext is ShellViewModel shell)
        {
            if (hint.Invoke is { } invoke)
            {
                invoke();
            }
            else
            {
                shell.Press(hint.Button);
            }
        }
    }
}

/// <summary>The dimmed backdrop and the card of <c>Shell.Modal</c>.</summary>
public partial class ModalHost : UserControl
{
    public ModalHost()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && IsLoaded)
            {
                Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
                Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            }
        };
    }

    private void OnBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.DismissModal();
        }
    }
}

/// <summary>The toasts of <c>Shell.Toasts</c>, sliding in at the top right.</summary>
public partial class ToastHost : UserControl
{
    public ToastHost() => InitializeComponent();
}
