// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mkwmp.App.Input;

namespace Mkwmp.App.Controls;

/// <summary>
/// A long list whose lines are focus stops (the file picker's): only the lines on screen exist
/// (it is virtualized, so a folder of two thousand files costs what twenty do), which the
/// directional focus cannot see past. So up and down go by index here (<see cref="Step"/>: the
/// window asks first), A or a click or a tap on a line raises <see cref="Activated"/>, and the
/// second click of a double-click is not a click on whatever the first one brought under the pointer.
/// </summary>
public sealed class EntryList : ListBox
{
    /// <summary>Clicks this soon after one that activated a line are its double-click's second half.</summary>
    private const int DoubleClickMs = 450;

    private object? _pressed;
    private long _lastClick = -DoubleClickMs;

    public EntryList()
    {
        Focusable = false;
        FocusNav.SetIsGroup(this, true);
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPressed), true);
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnReleased), true);
    }

    /// <summary>A line was chosen (its item): A / Enter on it, a click, a tap.</summary>
    public event EventHandler<object>? Activated;

    /// <summary>The focus one line up (-1) or down (+1); false at either end (the window then looks outside the list).</summary>
    public bool Step(ListBoxItem from, int delta)
    {
        var index = ItemContainerGenerator.IndexFromContainer(from);
        return index >= 0 && FocusIndex(index + delta);
    }

    /// <summary>The focus on a line by its index (it is scrolled to and made first when it was not on screen).</summary>
    public bool FocusIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return false;
        }
        ScrollIntoView(Items[index]);
        UpdateLayout();
        return ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem line && FocusNav.Focus(line);
    }

    /// <summary>A / Enter on the focused line.</summary>
    public void Activate(ListBoxItem line)
    {
        if (ItemContainerGenerator.ItemFromContainer(line) is { } item && item != DependencyProperty.UnsetValue)
        {
            Activated?.Invoke(this, item);
        }
    }

    private void OnPressed(object sender, MouseButtonEventArgs e) => _pressed = ItemUnder(e.OriginalSource);

    private void OnReleased(object sender, MouseButtonEventArgs e)
    {
        var item = ItemUnder(e.OriginalSource);
        var pressed = _pressed;
        _pressed = null;
        if (item is null || !ReferenceEquals(item, pressed) || Tap.Dragged)
        {
            return; // pressed elsewhere, or the end of a drag (Input/Tap.cs): not a tap on this line
        }
        var now = Environment.TickCount64;
        if (now - _lastClick < DoubleClickMs)
        {
            return;
        }
        _lastClick = now;
        Activated?.Invoke(this, item);
    }

    private object? ItemUnder(object source)
    {
        for (var at = source as DependencyObject; at is not null && !ReferenceEquals(at, this); at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is ListBoxItem line)
            {
                return ItemContainerGenerator.ItemFromContainer(line) is { } item && item != DependencyProperty.UnsetValue ? item : null;
            }
        }
        return null;
    }
}
