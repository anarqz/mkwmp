// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.Input;

/// <summary>
/// The motion of one scroll axis, stepped by the render clock with the time since the last frame
/// (so 30, 60 or 144 frames a second, under Wine too, move it the same way). Plain math, no WPF:
/// the tests link this file.
/// <list type="bullet">
/// <item>Gliding: an exponential approach to <see cref="Target"/> (time constant <see cref="GlideTime"/>):
/// fast at once, easing out. A wheel notch, the focus to keep in view, a stick: they move the
/// target; notches that come while it moves add to the target, so a fast spin chains into one glide.</item>
/// <item>Coasting: what a flick of the finger (or a mouse drag) leaves, slowed by friction
/// (<see cref="CoastTime"/>), stopped at the ends.</item>
/// </list>
/// The end of the range (<c>max</c>) is given at every step: content that grows or shrinks
/// mid-motion only clamps it, no jump.
/// </summary>
public sealed class ScrollMotion
{
    /// <summary>
    /// How much further a wheel notch goes than WPF's own (3 lines of 16 units = 48): 64 units.
    /// Players found the lists slow to get through (Settings: four full scrolls to its end).
    /// </summary>
    public const double SpeedFactor = 4.0 / 3.0;

    /// <summary>WPF's line (ScrollViewer.LineUp), in units.</summary>
    public const double LineSize = 16;

    /// <summary>Time constant of a glide: about 95% of the way after three of these (195 ms).</summary>
    public const double GlideTime = 0.065;

    /// <summary>Time constant of the friction after a flick: it travels velocity × this before it stops.</summary>
    public const double CoastTime = 0.325;

    private const double Settle = 0.25;
    private const double MinCoastSpeed = 12;

    private bool _coasting;

    public double Position { get; private set; }

    public double Target { get; private set; }

    /// <summary>Units per second while coasting; 0 otherwise.</summary>
    public double Velocity { get; private set; }

    public bool Moving => _coasting || Math.Abs(Target - Position) > Settle;

    /// <summary>
    /// How far one wheel event goes: <paramref name="delta"/> in Windows' units (120 a notch; a
    /// precision touchpad sends small ones, each counted as its share of a notch), up is positive.
    /// <paramref name="lines"/> is the system's "lines per notch"; -1 (a screen at a time) is <paramref name="viewport"/>.
    /// Returns the change of offset: up (a positive delta) scrolls back (negative).
    /// </summary>
    public static double WheelDistance(int delta, int lines, double viewport)
    {
        var notch = lines < 0 ? Math.Max(LineSize, viewport * 0.85) : Math.Max(1, lines) * LineSize * SpeedFactor;
        return -delta / 120.0 * notch;
    }

    /// <summary>The offset is <paramref name="offset"/> now, nothing moves (the layout changed it, a drag holds it).</summary>
    public void Jump(double offset)
    {
        Position = Target = offset;
        Velocity = 0;
        _coasting = false;
    }

    /// <summary>Glide to <paramref name="target"/>, from where it is now (a coast becomes a glide).</summary>
    public void GlideTo(double target, double max)
    {
        _coasting = false;
        Velocity = 0;
        Target = Math.Clamp(target, 0, Math.Max(0, max));
    }

    /// <summary>A wheel notch or a stick's step: added to where it was going, so quick ones chain.</summary>
    public void GlideBy(double delta, double max)
    {
        var from = _coasting ? Position : Target;
        GlideTo(from + delta, max);
    }

    /// <summary>A flick: coasts from here at <paramref name="velocity"/> units a second.</summary>
    public void Fling(double velocity)
    {
        Target = Position;
        if (Math.Abs(velocity) < MinCoastSpeed)
        {
            Velocity = 0;
            _coasting = false;
            return;
        }
        Velocity = velocity;
        _coasting = true;
    }

    /// <summary>Where a flick at <paramref name="velocity"/> comes to rest, before the ends.</summary>
    public static double CoastDistance(double velocity) => velocity * CoastTime;

    /// <summary>
    /// One frame, <paramref name="dt"/> seconds after the last. Returns the new offset; <see cref="Moving"/>
    /// is false once it arrived (the caller can stop asking for frames).
    /// </summary>
    public double Step(double dt, double max)
    {
        max = Math.Max(0, max);
        dt = Math.Clamp(dt, 0, 0.1); // a long frame (a hitch) moves as far as a tenth of a second
        if (_coasting)
        {
            var decay = Math.Exp(-dt / CoastTime);
            Position += Velocity * CoastTime * (1 - decay); // the exact integral: frame-rate independent
            Velocity *= decay;
            if (Position <= 0 || Position >= max || Math.Abs(Velocity) < MinCoastSpeed)
            {
                Position = Math.Clamp(Position, 0, max);
                Velocity = 0;
                _coasting = false;
            }
            Target = Position;
            return Position;
        }
        Target = Math.Clamp(Target, 0, max);
        Position = Math.Clamp(Position, 0, max);
        Position = Target + (Position - Target) * Math.Exp(-dt / GlideTime);
        if (Math.Abs(Target - Position) <= Settle)
        {
            Position = Target;
        }
        return Position;
    }

    // ── the right stick, and the D-pad / left stick held ──

    /// <summary>The right stick's dead zone (of a full tilt).</summary>
    public const double StickDeadZone = 0.2;

    /// <summary>A full tilt, when it was just pushed: units a second (about two notches' worth every tenth of a second).</summary>
    public const double StickSpeed = 1300;

    /// <summary>Held at full tilt, the stick gets this much faster, over <see cref="StickRampTime"/>.</summary>
    public const double StickBoost = 2.2;

    public const double StickRampTime = 1.2;

    /// <summary>
    /// The scroll speed of a stick (units a second, positive: forward/down) for its tilt
    /// <paramref name="axis"/> (-1..1, up positive as XInput gives it) held for <paramref name="held"/> seconds.
    /// Squared past the dead zone: fine control near the middle, full speed at the edge.
    /// </summary>
    public static double StickVelocity(double axis, double held)
    {
        var magnitude = Math.Abs(axis);
        if (magnitude <= StickDeadZone)
        {
            return 0;
        }
        var t = Math.Min(1, (magnitude - StickDeadZone) / (1 - StickDeadZone));
        var ramp = 1 + (StickBoost - 1) * Math.Clamp(held / StickRampTime, 0, 1);
        return -Math.Sign(axis) * t * t * StickSpeed * ramp;
    }

    /// <summary>The first repeat of a held direction, and the fastest one (it speeds up while held).</summary>
    public static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(380);

    public static readonly TimeSpan SlowRepeat = TimeSpan.FromMilliseconds(105);

    public static readonly TimeSpan FastRepeat = TimeSpan.FromMilliseconds(55);

    /// <summary>The time to the next repeat of a direction held for <paramref name="held"/> (since the first repeat): 105 ms easing to 55 ms over 1.5 s.</summary>
    public static TimeSpan RepeatInterval(TimeSpan held)
    {
        var t = Math.Clamp(held.TotalSeconds / 1.5, 0, 1);
        return TimeSpan.FromMilliseconds(SlowRepeat.TotalMilliseconds + (FastRepeat.TotalMilliseconds - SlowRepeat.TotalMilliseconds) * t);
    }
}
