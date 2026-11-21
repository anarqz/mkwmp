// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mkwmp.App.Input;

namespace Mkwmp.App.Controls;

/// <summary>
/// The launcher's scroll viewer. Every way of scrolling glides, on the render clock
/// (<see cref="ScrollMotion"/>: the same motion at any frame rate, under Wine too):
/// <list type="bullet">
/// <item>the focus is kept in view (<see cref="EdgePadding"/> of room around it; <see cref="Centered"/>
/// keeps it in the middle, for a carousel);</item>
/// <item>the wheel: a third further a notch than WPF's own; notches that come while it moves add up;
/// a precision touchpad's small steps count as their share of a notch;</item>
/// <item>the right stick (<see cref="ScrollBy"/>, from the window);</item>
/// <item>a finger (or the mouse) dragged along the list moves it, and a flick coasts. A drag never
/// clicks what it started on, and does not leave the focus there; a tap on a coasting list only stops it.
/// Touch arrives as the mouse (WPF promotes it; under Wine it is the mouse already), so both are one path.</item>
/// </list>
/// <c>Padding</c> is applied as the margin of the content (do not set a Margin on the content element yourself).
/// </summary>
public sealed class SmoothScrollViewer : ScrollViewer
{
    /// <summary>How far (units) a press moves along the list before it is a drag and not a tap.</summary>
    private const double DragStart = 12;

    public static readonly DependencyProperty EdgePaddingProperty = Dp.Register<SmoothScrollViewer, double>(nameof(EdgePadding), 48.0);
    public static readonly DependencyProperty CenteredProperty = Dp.Register<SmoothScrollViewer, bool>(nameof(Centered), false);

    private readonly ScrollMotion _vertical = new();
    private readonly ScrollMotion _horizontal = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastFrame;
    private bool _ticking;

    // a press that may become a drag
    private Point? _press;
    private double _pressOffset;
    private bool _pressVertical;
    private bool _dragging;
    private IInputElement? _focusBefore;
    private readonly List<(TimeSpan At, double Position)> _samples = [];

    static SmoothScrollViewer()
    {
        PaddingProperty.OverrideMetadata(typeof(SmoothScrollViewer),
            new FrameworkPropertyMetadata(default(Thickness), (d, _) => ((SmoothScrollViewer)d).ApplyPadding()));
        // before ScrollViewer's own class handler, which would jump
        EventManager.RegisterClassHandler(typeof(SmoothScrollViewer), RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((sender, e) => ((SmoothScrollViewer)sender).OnBringIntoView(e)));
    }

    public SmoothScrollViewer()
    {
        Focusable = false;
        // the finger is a drag here (this class's), not a right click when held, nor a system flick
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Unloaded += (_, _) => StopTicking();
    }

    public double EdgePadding
    {
        get => (double)GetValue(EdgePaddingProperty);
        set => SetValue(EdgePaddingProperty, value);
    }

    public bool Centered
    {
        get => (bool)GetValue(CenteredProperty);
        set => SetValue(CenteredProperty, value);
    }

    public static readonly DependencyProperty FadeEdgesProperty = Dp.Register<SmoothScrollViewer, double>(nameof(FadeEdges), 0.0,
        (d, _) => ((SmoothScrollViewer)d).UpdateFade());

    /// <summary>
    /// How far (in units) the content fades out at an edge it is scrolled past, so an entry cut by
    /// the edge melts away instead of showing half; 0 for no fade. An edge with nothing beyond it stays sharp.
    /// </summary>
    public double FadeEdges
    {
        get => (double)GetValue(FadeEdgesProperty);
        set => SetValue(FadeEdgesProperty, value);
    }

    /// <summary>Glides can play (else every scroll is a jump: the system's animations are off, or it is not on screen).</summary>
    private bool Animated => IsLoaded && SystemParameters.ClientAreaAnimation;

    private bool ScrollsVertically => ScrollableHeight > 0 && VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

    private bool ScrollsHorizontally => ScrollableWidth > 0 && HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;

    /// <summary>
    /// Scrolls by (<paramref name="dx"/>, <paramref name="dy"/>) units, gliding; added to a glide
    /// under way. False when it cannot go that way (an end, nothing to scroll): a caller can try an outer list.
    /// </summary>
    public bool ScrollBy(double dx, double dy)
    {
        var moved = false;
        if (dy != 0 && ScrollsVertically && CanGo(_vertical, VerticalOffset, ScrollableHeight, dy))
        {
            GlideBy(_vertical, VerticalOffset, ScrollableHeight, dy);
            moved = true;
        }
        if (dx != 0 && ScrollsHorizontally && CanGo(_horizontal, HorizontalOffset, ScrollableWidth, dx))
        {
            GlideBy(_horizontal, HorizontalOffset, ScrollableWidth, dx);
            moved = true;
        }
        return moved;
    }

    private static bool CanGo(ScrollMotion motion, double offset, double max, double delta)
    {
        var from = motion.Moving ? motion.Target : offset;
        return delta < 0 ? from > 0.5 : from < max - 0.5;
    }

    // ── the wheel ──

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Handled)
        {
            return;
        }
        // a list that scrolls only sideways (the carousel) takes the wheel sideways
        var vertical = ScrollsVertically || !ScrollsHorizontally;
        var distance = ScrollMotion.WheelDistance(e.Delta, SystemParameters.WheelScrollLines, vertical ? ViewportHeight : ViewportWidth);
        // at its end the wheel goes on to the list around this one
        e.Handled = vertical ? ScrollBy(0, distance) : ScrollBy(distance, 0);
    }

    // ── the focus kept in view ──

    private void OnBringIntoView(RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not Visual target || Content is not Visual content || ReferenceEquals(target, this) || !target.IsDescendantOf(content))
        {
            return;
        }
        e.Handled = true;
        if (_dragging)
        {
            return; // the finger holds the list
        }
        var size = target is UIElement element ? element.RenderSize : default;
        // in the coordinates of the scrolled extent: where it is in the viewport now, plus how far that is scrolled
        var box = target.TransformToAncestor(this).TransformBounds(e.TargetRect.IsEmpty ? new Rect(size) : e.TargetRect);
        box.Offset(HorizontalOffset, VerticalOffset);
        if (ScrollableHeight > 0)
        {
            GlideTo(_vertical, VerticalOffset, ScrollableHeight, Aim(box.Top, box.Bottom, VerticalOffset, ViewportHeight, ScrollableHeight));
        }
        if (ScrollableWidth > 0)
        {
            GlideTo(_horizontal, HorizontalOffset, ScrollableWidth, Aim(box.Left, box.Right, HorizontalOffset, ViewportWidth, ScrollableWidth));
        }
    }

    private double Aim(double near, double far, double offset, double viewport, double scrollable)
    {
        double aim;
        if (Centered)
        {
            aim = (near + far) / 2 - viewport / 2;
        }
        else
        {
            var pad = Math.Min(EdgePadding, Math.Max(0, (viewport - (far - near)) / 2));
            aim = near - pad < offset ? near - pad : far + pad > offset + viewport ? far + pad - viewport : offset;
        }
        return Math.Clamp(aim, 0, scrollable);
    }

    // ── the motion, on the render clock ──

    private void GlideTo(ScrollMotion motion, double offset, double max, double target)
    {
        if (!motion.Moving)
        {
            motion.Jump(offset); // from where the list is (the layout, the scroll bar may have moved it)
        }
        if (!Animated)
        {
            motion.Jump(Math.Clamp(target, 0, max));
            Apply();
            return;
        }
        motion.GlideTo(target, max);
        StartTicking();
    }

    private void GlideBy(ScrollMotion motion, double offset, double max, double delta)
    {
        if (!motion.Moving)
        {
            motion.Jump(offset);
        }
        if (!Animated)
        {
            motion.Jump(Math.Clamp(offset + delta, 0, max));
            Apply();
            return;
        }
        motion.GlideBy(delta, max);
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking)
        {
            return;
        }
        _ticking = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopTicking()
    {
        if (_ticking)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // the frame's own time (paced by the compositor, so steadier than a stopwatch read here);
        // Rendering can come twice for one frame: the second moves nothing
        var now = e is RenderingEventArgs r ? r.RenderingTime : _clock.Elapsed;
        if (_lastFrame is not { } last || now < last)
        {
            // the first frame of a motion: it starts from this one. Moving it a frame's worth at once
            // (not waiting a frame) keeps a wheel notch as quick to answer as it was.
            _lastFrame = now;
            Advance(1.0 / 60);
            return;
        }
        var dt = (now - last).TotalSeconds;
        if (dt <= 0)
        {
            return;
        }
        _lastFrame = now;
        Advance(dt);
    }

    private void Advance(double dt)
    {
        if (_vertical.Moving)
        {
            _vertical.Step(dt, ScrollableHeight);
        }
        if (_horizontal.Moving)
        {
            _horizontal.Step(dt, ScrollableWidth);
        }
        Apply();
        if (!_vertical.Moving && !_horizontal.Moving)
        {
            StopTicking();
        }
    }

    private void Apply()
    {
        if (Math.Abs(VerticalOffset - _vertical.Position) > 0.01 && ScrollableHeight > 0)
        {
            ScrollToVerticalOffset(_vertical.Position);
        }
        if (Math.Abs(HorizontalOffset - _horizontal.Position) > 0.01 && ScrollableWidth > 0)
        {
            ScrollToHorizontalOffset(_horizontal.Position);
        }
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        UpdateFade();
        // moved by something else (the layout, the scroll bar, content that shrank): the motion starts from there next time
        if (!_vertical.Moving && !_dragging && Math.Abs(_vertical.Position - VerticalOffset) > 0.5)
        {
            _vertical.Jump(VerticalOffset);
        }
        if (!_horizontal.Moving && !_dragging && Math.Abs(_horizontal.Position - HorizontalOffset) > 0.5)
        {
            _horizontal.Jump(HorizontalOffset);
        }
    }

    // ── a finger, or the mouse, dragged along the list ──

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseLeftButtonDown(e);
        _press = null;
        if (e.Handled || TakesItsOwnDrag(e.OriginalSource as DependencyObject))
        {
            return;
        }
        var coasting = Math.Abs(_vertical.Velocity) + Math.Abs(_horizontal.Velocity) > 150;
        if (_vertical.Moving || _horizontal.Moving)
        {
            // a touch stops a moving list where it is
            _vertical.Jump(VerticalOffset);
            _horizontal.Jump(HorizontalOffset);
            StopTicking();
        }
        if (coasting)
        {
            e.Handled = true; // and that is all it does: no click on what happened to pass under the finger
        }
        _press = e.GetPosition(this);
        _focusBefore = Keyboard.FocusedElement;
        _samples.Clear();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseMove(e);
        if (_press is not { } press)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(fling: false);
            return;
        }
        var at = e.GetPosition(this);
        if (!_dragging)
        {
            var dx = at.X - press.X;
            var dy = at.Y - press.Y;
            bool vertical;
            if (ScrollsVertically && Math.Abs(dy) >= DragStart && Math.Abs(dy) > Math.Abs(dx))
            {
                vertical = true;
            }
            else if (ScrollsHorizontally && Math.Abs(dx) >= DragStart && Math.Abs(dx) > Math.Abs(dy))
            {
                vertical = false;
            }
            else
            {
                return;
            }
            if (InnerListTakes(e.OriginalSource as DependencyObject, vertical))
            {
                _press = null; // a list inside this one scrolls that way: the drag is its
                return;
            }
            BeginDrag(at, vertical);
        }
        var position = _pressVertical ? at.Y : at.X;
        var origin = _pressVertical ? _press.Value.Y : _press.Value.X;
        var max = _pressVertical ? ScrollableHeight : ScrollableWidth;
        var motion = _pressVertical ? _vertical : _horizontal;
        motion.Jump(Math.Clamp(_pressOffset - (position - origin), 0, max));
        Apply();
        _samples.Add((_clock.Elapsed, position));
        if (_samples.Count > 12)
        {
            _samples.RemoveAt(0);
        }
        e.Handled = true;
    }

    private void BeginDrag(Point at, bool vertical)
    {
        _press = at; // from here: the list does not jump by the distance it took to become a drag
        _pressVertical = vertical;
        _pressOffset = vertical ? VerticalOffset : HorizontalOffset;
        _dragging = true;
        // taking the mouse from the button under the finger: it lets go without a click
        CaptureMouse();
        // the press may have put the focus on that button: a drag leaves it where it was
        if (_focusBefore is not null && !ReferenceEquals(Keyboard.FocusedElement, _focusBefore)
            && _focusBefore is UIElement { IsVisible: true })
        {
            Keyboard.Focus(_focusBefore);
        }
        _samples.Clear();
        _samples.Add((_clock.Elapsed, vertical ? at.Y : at.X));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragging)
        {
            if (_samples.Count > 0)
            {
                _samples.Add((_clock.Elapsed, _pressVertical ? e.GetPosition(this).Y : e.GetPosition(this).X));
            }
            EndDrag(fling: true);
            e.Handled = true; // the release of a drag is not a click
        }
        _press = null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            EndDrag(fling: false);
        }
    }

    private void EndDrag(bool fling)
    {
        _press = null;
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        var velocity = fling ? ReleaseVelocity() : 0;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        var motion = _pressVertical ? _vertical : _horizontal;
        if (velocity != 0 && Animated)
        {
            motion.Fling(-velocity); // the content follows the finger: the offset goes the other way
            StartTicking();
        }
    }

    /// <summary>Units a second at the release, from the last tenth of a second of the drag; 0 when the finger rested before it let go.</summary>
    private double ReleaseVelocity()
    {
        if (_samples.Count < 2)
        {
            return 0;
        }
        var last = _samples[^1];
        var first = _samples.First(s => (last.At - s.At).TotalSeconds <= 0.1);
        var seconds = (last.At - first.At).TotalSeconds;
        return seconds < 0.008 ? 0 : (last.Position - first.Position) / seconds;
    }

    /// <summary>Controls that a drag works on themselves: a slider's knob, a scroll bar, text.</summary>
    private bool TakesItsOwnDrag(DependencyObject? source)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, this); node = UpOf(node))
        {
            if (node is Slider or ScrollBar or Thumb or TextBoxBase or PasswordBox)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A list inside this one, under the finger, that can scroll that way itself.</summary>
    private bool InnerListTakes(DependencyObject? source, bool vertical)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, this); node = UpOf(node))
        {
            if (node is SmoothScrollViewer inner && (vertical ? inner.ScrollsVertically : inner.ScrollsHorizontally))
            {
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? UpOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    protected override void OnManipulationBoundaryFeedback(ManipulationBoundaryFeedbackEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Handled = true; // the window does not bounce at the end of a list
    }

    // ── the fade at the edges, the padding ──

    private (bool Top, bool Bottom, double Height) _fade;

    private void UpdateFade()
    {
        var height = ActualHeight;
        var fade = FadeEdges;
        var top = fade > 0 && VerticalOffset > 0.5;
        var bottom = fade > 0 && VerticalOffset < ScrollableHeight - 0.5;
        if (!top && !bottom || height <= 0)
        {
            _fade = default;
            ClearValue(OpacityMaskProperty);
            return;
        }
        if (_fade == (top, bottom, height) && OpacityMask is not null)
        {
            return;
        }
        _fade = (top, bottom, height);
        var edge = Math.Min(0.45, fade / height);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        // the outer part of the fade is fully clear: a sliver of an entry at the very edge does not show at all
        var clear = edge * 0.4;
        mask.GradientStops.Add(new GradientStop(top ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(top ? Colors.Transparent : Colors.Black, top ? clear : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, top ? edge : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, bottom ? 1 - edge : 1));
        mask.GradientStops.Add(new GradientStop(bottom ? Colors.Transparent : Colors.Black, bottom ? 1 - clear : 1));
        mask.GradientStops.Add(new GradientStop(bottom ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        OpacityMask = mask;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ApplyPadding();
    }

    /// <summary>
    /// The padding is room INSIDE the scrolled area (it becomes the content's margin), so a focus
    /// card that is wider than its row is not cut at the viewport's edge.
    /// </summary>
    private void ApplyPadding()
    {
        if (Content is FrameworkElement content && Padding != default)
        {
            content.Margin = Padding;
        }
    }
}
