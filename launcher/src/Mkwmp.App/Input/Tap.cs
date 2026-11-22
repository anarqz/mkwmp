// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mkwmp.App.Input;

/// <summary>
/// One rule for the pointer (the mouse, and the finger, which arrives as the mouse: promoted by WPF,
/// or already the mouse under Wine and gamescope): <b>everything acts when the press is let go, and
/// only if it was a tap</b> (<see cref="TapTracker"/>). The window is watched once
/// (<see cref="Attach"/>), so no control has its own version of this:
/// <list type="bullet">
/// <item>a button looks pressed at once, but the moment the press turns into a drag it is let go of
/// (no click when the finger lifts), and the focus goes back to where it was;</item>
/// <item>a list entry (<c>ListBoxItem</c>) takes the focus and is selected when the tap ends, not
/// when it begins (WPF's own selects on the press and then follows the pointer);</item>
/// <item>anything else that reacts to the pointer (a scrim that closes its layer, a text box that
/// opens the on-screen keyboard) asks <see cref="IsTap"/> in its mouse-up handler;</item>
/// <item><see cref="Controls.SmoothScrollViewer"/> starts its drag at the same moment, by <see cref="Dragged"/>.</item>
/// </list>
/// Controls that a drag works on themselves keep it: a slider's track, a scroll bar, text (<see cref="OwnsDrag"/>).
/// </summary>
public static class Tap
{
    private static readonly TapTracker s_tracker = new();
    private static DependencyObject? s_source;
    private static ListBoxItem? s_item;
    private static IInputElement? s_focusBefore;
    private static bool s_registered;

    /// <summary>The press under way (or the last one, until the next) became a drag: its release is not a click.</summary>
    public static bool Dragged => s_tracker.Dragged;

    /// <summary>Watches the window's pointer. Once per window; the class handlers once per process.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown), true);
        window.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), true);
        window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => s_tracker.Release()), true);
        if (s_registered)
        {
            return;
        }
        s_registered = true;
        // before ListBoxItem's own handling (a class handler of the derived type runs first): no selection, focus or capture on the press
        EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnItemDown));
        EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnItemUp));
    }

    /// <summary>
    /// For a mouse-up handler: the press began inside <paramref name="element"/> and never became a drag.
    /// (That the release is inside it too is the event's own doing.)
    /// </summary>
    public static bool IsTap(DependencyObject element)
    {
        if (Dragged)
        {
            return false;
        }
        for (var node = s_source; node is not null; node = Up(node))
        {
            if (ReferenceEquals(node, element))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Controls that a drag works on themselves, between <paramref name="source"/> and <paramref name="stop"/>: a slider's track, a scroll bar, text.</summary>
    public static bool OwnsDrag(DependencyObject? source, DependencyObject? stop = null)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, stop); node = Up(node))
        {
            if (node is Track or ScrollBar or Thumb or TextBoxBase or PasswordBox)
            {
                return true;
            }
        }
        return false;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition((IInputElement)sender);
        s_tracker.Press(at.X, at.Y);
        s_source = e.OriginalSource as DependencyObject;
        s_focusBefore = Keyboard.FocusedElement;
        s_item = null;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (!s_tracker.Pressed)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            s_tracker.Release(); // let go where the window did not see it
            return;
        }
        if (OwnsDrag(s_source))
        {
            return;
        }
        var at = e.GetPosition((IInputElement)sender);
        if (!s_tracker.Move(at.X, at.Y))
        {
            return;
        }
        // a drag now: the button under the finger lets go without a click (losing the capture ends its pressed state)...
        if (Mouse.Captured is ButtonBase button)
        {
            button.ReleaseMouseCapture();
        }
        // ...and the focus the press may have put on it goes back to where it was
        if (s_focusBefore is UIElement { IsVisible: true, IsEnabled: true } before && !ReferenceEquals(Keyboard.FocusedElement, before))
        {
            Keyboard.Focus(before);
        }
    }

    private static void OnItemDown(object sender, MouseButtonEventArgs e)
    {
        s_item = sender as ListBoxItem;
        e.Handled = true;
    }

    private static void OnItemUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(item, s_item) || Dragged || !item.IsEnabled)
        {
            return;
        }
        s_item = null;
        item.Focus();
        if (ItemsControl.ItemsControlFromItemContainer(item) is Selector list
            && list.ItemContainerGenerator.ItemFromContainer(item) is { } entry && entry != DependencyProperty.UnsetValue)
        {
            list.SetCurrentValue(Selector.SelectedItemProperty, entry);
        }
    }

    private static DependencyObject? Up(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
