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

    public OnlineLocked() => InitializeComponent();

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
