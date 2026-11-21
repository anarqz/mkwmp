// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views.Mods;

public partial class ModsPage : UserControl
{
    public ModsPage()
    {
        InitializeComponent();
        // which mod's row has the focus: X switches that one
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnAnyFocus), true);
    }

    private void OnAnyFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModsViewModel mods)
        {
            mods.Focused = (e.OriginalSource as FrameworkElement)?.DataContext as ModRow;
        }
    }
}