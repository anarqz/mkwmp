// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.Input;

/// <summary>
/// The motion of one scroll axis, stepped by the render clock with the time since the last frame
/// (so 30, 60 or 144 frames a second, under Wine too, move it the same way). Plain math, no WPF:
/// the tests link this file. One rule for all of it: <b>the velocity never jumps</b>. Whatever
/// starts a motion starts from the speed the list already has, so a held D-pad, a spun wheel or a
/// stick read 60 times a second all give one even movement instead of a row of small pushes.
/// <list type="bullet">
/// <item>Gliding: a critically damped spring to <see cref="Target"/> (<see cref="GlideTime"/>): it
/// picks up speed within a frame or two, eases out, and never swings back. A wheel notch, the focus
/// to keep in view: they move the target; those that come while it moves add to the target and keep
/// the speed, so a fast spin or a held direction is one glide.</item>
/// <item>Coasting: what a flick of the finger (or a mouse drag) leaves, slowed by friction
/// (<see cref="CoastTime"/>), stopped at the ends.</item>
/// <item>Driving: the right stick gives a speed (<see cref="Drive"/>), reached smoothly; let go
/// (or not heard from for <see cref="DriveTimeout"/>), the list settles a little further on.</item>
/// </list>
/// The end of the range (<c>max</c>) is given at every step: content that grows or shrinks
/// mid-motion only clamps it, no jump.
/// </summary>
public sealed class ScrollMotion
{
    /// <summary>
    /// How much further a wheel notch goes than WPF's own (3 lines of 16 units = 48): 80 units.
    /// Players found the lists slow to get through, twice: 64 first, then this.
    /// </summary>
    public const double SpeedFactor = 5.0 / 3.0;

    /// <summary>WPF's line (ScrollViewer.LineUp), in units.</summary>
    public const double LineSize = 16;

    /// <summary>
    /// The spring's time constant (1 / its angular frequency). From rest it is 80% of the way after
    /// three of these and 95% after about 4.7 (210 ms): as quick to arrive as before, without the jolt of a first frame.
    /// </summary>
    public const double GlideTime = 0.045;

    /// <summary>Time constant of the friction after a flick: it travels velocity × this before it stops.</summary>
    public const double CoastTime = 0.4;

    /// <summary>How quickly a driven list reaches the speed asked for (time constant).</summary>
    public const double DriveTime = 0.07;

    /// <summary>A drive that is not renewed for this long has ended (the stick's last word was lost).</summary>
    public const double DriveTimeout = 0.15;

    /// <summary>Let go, a driven list comes to rest this many seconds of its speed further on.</summary>
    public const double DriveStop = 0.1;

    private const double Settle = 0.25;
    private const double SettleSpeed = 6;
    private const double MinCoastSpeed = 12;

    private enum Mode { Glide, Coast, Drive }

    private Mode _mode;
    private double _drive;
    private double _driveAge;

    public double Position { get; private set; }

    public double Target { get; private set; }

    /// <summary>Units per second, now (gliding, coasting or driven); 0 at rest.</summary>
    public double Velocity { get; private set; }

    /// <summary>Coasting after a flick (a touch then only stops the list).</summary>
    public bool Coasting => _mode == Mode.Coast;

    public bool Moving => _mode != Mode.Glide || Math.Abs(Target - Position) > Settle || Math.Abs(Velocity) > SettleSpeed;

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
        _mode = Mode.Glide;
    }

    /// <summary>Glide to <paramref name="target"/>, from where it is and at the speed it has now (a coast becomes a glide).</summary>
    public void GlideTo(double target, double max)
    {
        _mode = Mode.Glide;
        Target = Math.Clamp(target, 0, Math.Max(0, max));
    }

    /// <summary>A wheel notch: added to where it was going, so quick ones chain.</summary>
    public void GlideBy(double delta, double max)
    {
        var from = _mode == Mode.Glide ? Target : Position;
        GlideTo(from + delta, max);
    }

    /// <summary>A flick: coasts from here at <paramref name="velocity"/> units a second.</summary>
    public void Fling(double velocity)
    {
        Target = Position;
        if (Math.Abs(velocity) < MinCoastSpeed)
        {
            Velocity = 0;
            _mode = Mode.Glide;
            return;
        }
        Velocity = velocity;
        _mode = Mode.Coast;
    }

    /// <summary>Where a flick at <paramref name="velocity"/> comes to rest, before the ends.</summary>
    public static double CoastDistance(double velocity) => velocity * CoastTime;

    /// <summary>
    /// The stick: move at <paramref name="velocity"/> units a second until told otherwise. Call it again
    /// while the stick is held (each poll); 0, or silence for <see cref="DriveTimeout"/>, lets go.
    /// </summary>
    public void Drive(double velocity, double max)
    {
        if (velocity == 0)
        {
            if (_mode == Mode.Drive)
            {
                Release(max);
            }
            return;
        }
        _mode = Mode.Drive;
        _drive = velocity;
        _driveAge = 0;
        Target = Position;
    }

    private void Release(double max) => GlideTo(Position + Velocity * DriveStop, max);

    /// <summary>
    /// One frame, <paramref name="dt"/> seconds after the last. Returns the new offset; <see cref="Moving"/>
    /// is false once it arrived (the caller can stop asking for frames).
    /// </summary>
    public double Step(double dt, double max)
    {
        max = Math.Max(0, max);
        dt = Math.Clamp(dt, 0, 0.1); // a long frame (a hitch) moves as far as a tenth of a second
        if (_mode == Mode.Drive)
        {
            _driveAge += dt;
            if (_driveAge > DriveTimeout)
            {
                Release(max);
            }
            else
            {
                // the speed eases to the one asked for; the exact integral of that: frame-rate independent
                var decay = Math.Exp(-dt / DriveTime);
                Position += _drive * dt + (Velocity - _drive) * DriveTime * (1 - decay);
                Velocity = _drive + (Velocity - _drive) * decay;
                StopAtEnds(max);
                Target = Position;
                return Position;
            }
        }
        if (_mode == Mode.Coast)
        {
            var decay = Math.Exp(-dt / CoastTime);
            Position += Velocity * CoastTime * (1 - decay); // the exact integral: frame-rate independent
            Velocity *= decay;
            if (StopAtEnds(max) || Math.Abs(Velocity) < MinCoastSpeed)
            {
                Velocity = 0;
                _mode = Mode.Glide;
            }
            Target = Position;
            return Position;
        }

        Target = Math.Clamp(Target, 0, max);
        Position = Math.Clamp(Position, 0, max);
        // a critically damped spring, solved exactly for this frame: x(t) = target + (a + b t) e^(-w t)
        var w = 1 / GlideTime;
        var a = Position - Target;
        var b = Velocity + w * a;
        var e = Math.Exp(-w * dt);
        Position = Target + (a + b * dt) * e;
        Velocity = (b - w * (a + b * dt)) * e;
        if (StopAtEnds(max) && (Target <= 0 || Target >= max))
        {
            Position = Target;
        }
        if (Math.Abs(Target - Position) <= Settle && Math.Abs(Velocity) <= SettleSpeed)
        {
            Position = Target;
            Velocity = 0;
        }
        return Position;
    }

    /// <summary>An end of the range is a wall: the list stops there. True when it was hit.</summary>
    private bool StopAtEnds(double max)
    {
        if (Position > 0 && Position < max)
        {
            return false;
        }
        Position = Math.Clamp(Position, 0, max);
        Velocity = 0;
        return true;
    }

    // ── the right stick, and the D-pad / left stick held ──

    /// <summary>The right stick's dead zone (of a full tilt).</summary>
    public const double StickDeadZone = 0.2;

    /// <summary>A full tilt, when it was just pushed: units a second (two wheel notches' worth every tenth of a second).</summary>
    public const double StickSpeed = 1600;

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
    public static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(340);

    public static readonly TimeSpan SlowRepeat = TimeSpan.FromMilliseconds(95);

    public static readonly TimeSpan FastRepeat = TimeSpan.FromMilliseconds(48);

    /// <summary>The time to the next repeat of a direction held for <paramref name="held"/> (since the first repeat): 95 ms easing to 48 ms over 1.5 s.</summary>
    public static TimeSpan RepeatInterval(TimeSpan held)
    {
        var t = Math.Clamp(held.TotalSeconds / 1.5, 0, 1);
        return TimeSpan.FromMilliseconds(SlowRepeat.TotalMilliseconds + (FastRepeat.TotalMilliseconds - SlowRepeat.TotalMilliseconds) * t);
    }
}
