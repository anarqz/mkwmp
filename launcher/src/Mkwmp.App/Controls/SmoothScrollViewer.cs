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
/// <item>the wheel: 80 units a notch (WPF's own: 48); notches that come while it moves add up;
/// a precision touchpad's small steps count as their share of a notch;</item>
/// <item>the right stick gives a speed (<see cref="ScrollAt"/>, from the window): the list moves every
/// frame, not at the rhythm the stick is read;</item>
/// <item>a finger (or the mouse) dragged along the list moves it, and a flick coasts. The drag begins
/// where every control agrees it does (<see cref="Tap"/>: past the touch slop), so it never clicks what
/// it started on, and does not leave the focus there; a tap on a coasting list only stops it.
/// Touch arrives as the mouse (WPF promotes it; under Wine it is the mouse already), so both are one path.</item>
/// </list>
/// The offset moves in fractions of a unit while it glides (<see cref="Apply"/>) and rests on whole ones.
/// <c>Padding</c> is applied as the margin of the content (do not set a Margin on the content element yourself).
/// </summary>
public sealed class SmoothScrollViewer : ScrollViewer
{
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

    /// <summary>
    /// Glides can play (else every scroll is a jump: the system's animations are off, or it is not on screen).
    /// Under Wine the system's switch is not the player's (nothing on a Steam Deck sets it): always.
    /// </summary>
    private bool Animated => IsLoaded && (Mkwmp.Core.Platform.WineHost.Detected || SystemParameters.ClientAreaAnimation);

    private bool ScrollsVertically => ScrollableHeight > 0 && VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

    private bool ScrollsHorizontally => ScrollableWidth > 0 && HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;

    /// <summary>
    /// Scrolls by (<paramref name="dx"/>, <paramref name="dy"/>) units, gliding; added to a glide
    /// under way. False when it cannot go that way (an end, nothing to scroll): a caller can try an outer list.
    /// </summary>
    public bool ScrollBy(double dx, double dy)
    {
        var moved = false;
        if (dy != 0 && ScrollsVertically && CanGo(_vertical.Moving ? _vertical.Target : VerticalOffset, ScrollableHeight, dy))
        {
            GlideBy(_vertical, VerticalOffset, ScrollableHeight, dy);
            moved = true;
        }
        if (dx != 0 && ScrollsHorizontally && CanGo(_horizontal.Moving ? _horizontal.Target : HorizontalOffset, ScrollableWidth, dx))
        {
            GlideBy(_horizontal, HorizontalOffset, ScrollableWidth, dx);
            moved = true;
        }
        return moved;
    }

    /// <summary>
    /// The right stick: scrolls at (<paramref name="vx"/>, <paramref name="vy"/>) units a second until told
    /// otherwise (call it at every poll while the stick is held; zeros let go, and so does silence). The list
    /// moves on the render clock, not at the rhythm of the calls. False when it cannot go that way.
    /// </summary>
    public bool ScrollAt(double vx, double vy)
    {
        // at an end (or with nothing to scroll) the axis is let go: the caller drives the list around this one
        var down = vy != 0 && ScrollsVertically && CanGo(_vertical.Moving ? _vertical.Position : VerticalOffset, ScrollableHeight, vy);
        var across = vx != 0 && ScrollsHorizontally && CanGo(_horizontal.Moving ? _horizontal.Position : HorizontalOffset, ScrollableWidth, vx);
        DriveAt(_vertical, VerticalOffset, ScrollableHeight, down ? vy : 0);
        DriveAt(_horizontal, HorizontalOffset, ScrollableWidth, across ? vx : 0);
        return down || across;
    }

    private void DriveAt(ScrollMotion motion, double offset, double max, double velocity)
    {
        if (_dragging)
        {
            return; // the finger holds the list
        }
        if (!motion.Moving)
        {
            if (velocity == 0)
            {
                return;
            }
            motion.Jump(offset);
        }
        if (!Animated)
        {
            motion.Jump(Math.Clamp(offset + velocity / 60, 0, max));
            Apply();
            return;
        }
        motion.Drive(velocity, max);
        StartTicking();
    }

    private static bool CanGo(double from, double max, double delta) => delta < 0 ? from > 0.5 : from < max - 0.5;

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
        box.Offset(HorizontalOffset + _nudge.X, VerticalOffset + _nudge.Y);
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
        // a whole unit: at rest the content sits on the layout's grid (sharp text)
        return Math.Round(Math.Clamp(aim, 0, scrollable));
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
        Step(_vertical, dt, ScrollableHeight);
        Step(_horizontal, dt, ScrollableWidth);
        Apply();
        if (!_vertical.Moving && !_horizontal.Moving)
        {
            StopTicking();
        }
    }

    private static void Step(ScrollMotion motion, double dt, double max)
    {
        if (!motion.Moving)
        {
            return;
        }
        motion.Step(dt, max);
        if (!motion.Moving)
        {
            motion.Jump(Math.Round(motion.Position)); // arrived: on a whole unit (at most half a unit away)
        }
    }

    /// <summary>
    /// Shows the motion's position. The layout places the content on whole units (the window rounds its
    /// layout, and a unit is two pixels on a 4K screen), which a slow glide shows as steps: so the scroll
    /// offset takes the whole units and a render transform the fraction. That costs no layout, and at rest
    /// the fraction is zero (the motion's targets are whole units), so text is as sharp as ever.
    /// </summary>
    private void Apply()
    {
        var (x, fx) = Split(_horizontal.Position, ScrollableWidth);
        var (y, fy) = Split(_vertical.Position, ScrollableHeight);
        if (Math.Abs(VerticalOffset - y) > 0.01 && ScrollableHeight > 0)
        {
            ScrollToVerticalOffset(y);
        }
        if (Math.Abs(HorizontalOffset - x) > 0.01 && ScrollableWidth > 0)
        {
            ScrollToHorizontalOffset(x);
        }
        Nudge(fx, fy);
    }

    private (double Whole, double Fraction) Split(double position, double max)
    {
        if (!_fine || max <= 0)
        {
            return (position, 0);
        }
        var whole = Math.Floor(position);
        var fraction = position - whole;
        return fraction < 0.004 ? (whole, 0) : (whole, fraction);
    }

    private readonly TranslateTransform _fineShift = new();
    private Vector _nudge;
    private bool _fine;

    /// <summary>The part of the offset below a whole unit, as a shift of the content.</summary>
    private void Nudge(double x, double y)
    {
        if (!_fine || (_nudge.X == x && _nudge.Y == y))
        {
            return;
        }
        _nudge = new Vector(x, y);
        _fineShift.X = -x;
        _fineShift.Y = -y;
    }

    /// <summary>The content takes the fine shift unless it has a transform of its own (then whole units it is).</summary>
    private void AdoptContent()
    {
        _fine = false;
        _nudge = default;
        _fineShift.X = _fineShift.Y = 0;
        if (Content is UIElement content && !CanContentScroll
            && (content.RenderTransform is null || content.RenderTransform == Transform.Identity || ReferenceEquals(content.RenderTransform, _fineShift)))
        {
            content.RenderTransform = _fineShift;
            _fine = true;
        }
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        UpdateFade();
        // moved by something else (the layout, the scroll bar, content that shrank): the motion starts from there next time
        if (!_vertical.Moving && !_dragging && Math.Abs(_vertical.Position - VerticalOffset) > 1)
        {
            _vertical.Jump(VerticalOffset);
            Nudge(_nudge.X, 0);
        }
        if (!_horizontal.Moving && !_dragging && Math.Abs(_horizontal.Position - HorizontalOffset) > 1)
        {
            _horizontal.Jump(HorizontalOffset);
            Nudge(0, _nudge.Y);
        }
    }

    // ── a finger, or the mouse, dragged along the list ──

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseLeftButtonDown(e);
        _press = null;
        if (e.Handled || Tap.OwnsDrag(e.OriginalSource as DependencyObject, this))
        {
            return;
        }
        var coasting = (_vertical.Coasting || _horizontal.Coasting) && Math.Abs(_vertical.Velocity) + Math.Abs(_horizontal.Velocity) > 150;
        if (_vertical.Moving || _horizontal.Moving)
        {
            // a touch stops a moving list where it is
            _vertical.Jump(Math.Round(_vertical.Position));
            _horizontal.Jump(Math.Round(_horizontal.Position));
            StopTicking();
            Apply();
        }
        if (coasting)
        {
            e.Handled = true; // and that is all it does: no click on what happened to pass under the finger
        }
        _press = e.GetPosition(this);
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
            // the same moment for every control: the press is a drag once it left the touch slop (Input/Tap.cs)
            if (!Tap.Dragged)
            {
                return;
            }
            var dx = at.X - press.X;
            var dy = at.Y - press.Y;
            bool vertical;
            if (ScrollsVertically && Math.Abs(dy) > Math.Abs(dx))
            {
                vertical = true;
            }
            else if (ScrollsHorizontally && Math.Abs(dx) > Math.Abs(dy))
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
        var motion = vertical ? _vertical : _horizontal;
        motion.Jump(motion.Moving ? motion.Position : vertical ? VerticalOffset : HorizontalOffset); // whatever moved it stops: the finger has it
        _pressOffset = motion.Position;
        _dragging = true;
        // the pointer is this list's until it is let go (Tap already took it from the button under the finger)
        CaptureMouse();
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
        else
        {
            // let go at rest: onto a whole unit (the fine shift back to zero)
            motion.Jump(Math.Round(motion.Position));
            Apply();
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
        AdoptContent();
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
