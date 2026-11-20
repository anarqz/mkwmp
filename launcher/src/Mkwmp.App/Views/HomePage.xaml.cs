// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using Mkwmp.App.Controls;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views;

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private void OnTileFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Tile tile && DataContext is HomeViewModel home)
        {
            home.SelectedTitle = tile.Title;
        }
    }
}
