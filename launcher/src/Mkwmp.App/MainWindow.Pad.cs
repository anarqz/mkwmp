// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Media;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;

namespace Mkwmp.App;

/// <summary>The controller beyond its buttons: the game's button layout, and the right stick that scrolls.</summary>
public partial class MainWindow
{
    /// <summary>
    /// The window's buttons follow the game's Settings › Controls › Button layout, as the game applies
    /// it to the controller in use (<see cref="PadLayout"/>): with Nintendo the right face button
    /// confirms, with Xbox the bottom one, and Automatic picks as the game does. Read where the launcher
    /// said something changed (any thread: after the first read, the core's view is a field).
    /// </summary>
    private void FollowPadLayout()
    {
        var setting = _vm.Launcher.GameSettings.Value(PadLayout.SettingId);
        Dispatcher.BeginInvoke(() =>
        {
            if (_gamepad.Setting != setting)
            {
                _gamepad.Setting = setting;
            }
        });
    }

    private void LogPadMapping() =>
        App.Log($"input: button layout {_gamepad.Mapping} (pad_layout = {(_gamepad.Setting is { Length: > 0 } s ? s : "auto")}, controller {(_gamepad.Connected ? _gamepad.Identity.ToString() : "none")})");

    private void OnLauncherChangedForPad(object? sender, EventArgs e) => FollowPadLayout();

    /// <summary>
    /// The right stick (a speed, units a second) scrolls the list that has the focus (or, when the focus is not in one, the
    /// largest list of the layer in front: Settings' pane from its categories). At its end, the list around it.
    /// </summary>
    private void ScrollWithStick(ScrollStep step)
    {
        if (IsOverlayAnimating)
        {
            return;
        }
        var root = ActiveRoot;
        if (step == default)
        {
            _stickViewer?.ScrollAt(0, 0); // the stick was let go
            _stickViewer = null;
            return;
        }
        for (DependencyObject? node = FocusNav.Current(this); node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
        {
            if (node is SmoothScrollViewer viewer && Drive(viewer, step))
            {
                return;
            }
        }
        SmoothScrollViewer? largest = null;
        foreach (var viewer in Viewers(root))
        {
            if (viewer.IsVisible && (step.Y != 0 && viewer.ScrollableHeight > 0 || step.X != 0 && viewer.ScrollableWidth > 0)
                && (largest is null || viewer.ViewportHeight * viewer.ViewportWidth > largest.ViewportHeight * largest.ViewportWidth))
            {
                largest = viewer;
            }
        }
        if (largest is null || !Drive(largest, step))
        {
            Drive(null, step);
        }
    }

    private SmoothScrollViewer? _stickViewer;

    /// <summary>The stick's speed to <paramref name="viewer"/>; the list it drove before is let go when it is another.</summary>
    private bool Drive(SmoothScrollViewer? viewer, ScrollStep step)
    {
        if (!ReferenceEquals(viewer, _stickViewer))
        {
            _stickViewer?.ScrollAt(0, 0);
            _stickViewer = null;
        }
        if (viewer is null || !viewer.ScrollAt(step.X, step.Y))
        {
            return false;
        }
        _stickViewer = viewer;
        return true;
    }

    private static IEnumerable<SmoothScrollViewer> Viewers(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }
            if (child is SmoothScrollViewer viewer)
            {
                yield return viewer;
            }
            foreach (var inner in Viewers(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>
    /// A button of the harness' <c>--press</c>: a role ("a", "b", "down"...), or a face button by
    /// where it is ("south", "east", "west", "north"), which the button layout turns into a role as the controller's would be.
    /// </summary>
    private PadButton? HarnessButton(string name)
    {
        if (PadLayout.FromPosition(name) is { } position)
        {
            return PadLayout.ToRole(position, _gamepad.Mapping.Layout);
        }
        return Enum.TryParse<PadButton>(name, ignoreCase: true, out var button) ? button : null;
    }
}
