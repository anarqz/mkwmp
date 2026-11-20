// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views;

public partial class FriendsPanel : UserControl
{
    public FriendsPanel() => InitializeComponent();

    /// <summary>Enter in the field, or OK on the on-screen keyboard.</summary>
    private void OnSubmitted(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        (DataContext as FriendsPanelViewModel)?.Submit();
    }
}
