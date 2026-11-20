// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views.Settings;

public partial class ServerList : UserControl
{
    private ServerPicker? _picker;

    public ServerList()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Follow(DataContext as ServerPicker);
        Unloaded += (_, _) => Follow(null);
        Loaded += (_, _) => Follow(DataContext as ServerPicker);
    }

    private void Follow(ServerPicker? picker)
    {
        if (ReferenceEquals(_picker, picker))
        {
            return;
        }
        if (_picker is not null)
        {
            _picker.FocusRequested -= OnFocusRequested;
        }
        _picker = picker;
        if (picker is not null)
        {
            picker.FocusRequested += OnFocusRequested;
        }
    }

    /// <summary>A server was hidden, or more rows came: the focus goes to the row at that place, once it is there.</summary>
    private void OnFocusRequested(object? sender, int index) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (RowsHost.Items.Count == 0)
        {
            return;
        }
        var container = RowsHost.ItemContainerGenerator.ContainerFromIndex(Math.Min(index, RowsHost.Items.Count - 1));
        (FirstButton(container) ?? FirstButton(RowsHost))?.Focus();
    });

    private static Button? FirstButton(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Focusable: true, IsVisible: true } button)
            {
                return button;
            }
            if (FirstButton(child) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }

    /// <summary>The picker knows the row under the focus: Y hides that one, and the hint bar says so.</summary>
    private void OnRowFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is ServerPicker picker)
        {
            picker.Focused = (sender as FrameworkElement)?.DataContext as ServerRow;
        }
    }

    private void OnRowBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is ServerPicker picker && ReferenceEquals(picker.Focused, (sender as FrameworkElement)?.DataContext))
        {
            picker.Focused = null;
        }
    }
}
