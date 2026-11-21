// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views;

/// <summary>
/// The file picker's panel. The view model decides everything; here is only where the focus goes
/// when a folder's list arrives (its first line, or the line the player came from; "Use This
/// Folder" when the folder is a right one; never a stop outside the panel), and which line has it.
/// </summary>
public partial class PickerSidebar : UserControl
{
    private PickerViewModel? _picker;
    private int _lastLine;

    public PickerSidebar()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_picker is not null)
            {
                _picker.FocusRequested -= OnFocusRequested;
            }
            _picker = e.NewValue as PickerViewModel;
            if (_picker is not null)
            {
                _picker.FocusRequested += OnFocusRequested;
            }
        };
        List.AddHandler(GotFocusEvent, new RoutedEventHandler(OnLineFocus));
        AddHandler(GotFocusEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not ListBoxItem)
            {
                _picker?.Focused(null);
            }
        }));
    }

    private void OnLineFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListBoxItem line)
        {
            _lastLine = Math.Max(0, List.ItemContainerGenerator.IndexFromContainer(line));
            _picker?.Focused(line.DataContext as PickerEntry);
        }
    }

    private void OnActivated(object? sender, object item)
    {
        if (item is PickerEntry entry)
        {
            _picker?.Activate(entry);
        }
    }

    /// <summary>Enter in the path box, or OK on the on-screen keyboard.</summary>
    private void OnPathSubmitted(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _picker?.Go();
    }

    private void OnFocusRequested(object? sender, PickerFocus focus) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(sender, _picker) || _picker is not { } picker || !IsVisible || picker.Shell.IsKeyboardOpen || !picker.Shell.IsPickerOpen)
            {
                return;
            }
            UpdateLayout();
            // a path longer than its box shows its end: the folder that is open
            PathBox.CaretIndex = PathBox.Text.Length;
            PathBox.ScrollToHorizontalOffset(1e6);
            switch (focus.Zone)
            {
                case PickerZone.Places:
                    FocusPlace();
                    break;
                case PickerZone.UseFolder when FocusNav.Focus(UseButton):
                    break;
                default:
                    // an empty list: the folder itself as the pick, else the way out of it
                    // (RB: back to the line the focus was on)
                    if (!List.FocusIndex(focus.Index < 0 ? _lastLine : focus.Index) && !List.FocusIndex(0) && !FocusNav.Focus(UseButton) && !FocusNav.Focus(UpButton))
                    {
                        FocusPlace();
                    }
                    break;
            }
        });

    /// <summary>The place the open folder is in, else the first one.</summary>
    private void FocusPlace()
    {
        var stops = FocusNav.Stops(PlacesList).ToList();
        FocusNav.Focus(stops.FirstOrDefault(s => s is FrameworkElement { DataContext: PickerPlace { IsCurrent: true } }) ?? stops.FirstOrDefault());
    }
}
