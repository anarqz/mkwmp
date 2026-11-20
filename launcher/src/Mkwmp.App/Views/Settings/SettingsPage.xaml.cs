// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views.Settings;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // which game-setting row has the focus: Y resets that one
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnAnyFocus), true);
    }

    private void OnAnyFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            settings.FocusedGameRow = (e.OriginalSource as FrameworkElement)?.DataContext as GameSettingRow;
        }
    }
}
