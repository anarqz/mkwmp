// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.Input;

/// <summary>
/// Tap or drag: the one decision behind every press of the pointer (<see cref="Tap"/> feeds it the
/// mouse, which is also the finger). A press that stays within <see cref="Slop"/> of where it began
/// is a tap, and acts when it is let go; one that moves further is a drag for the rest of that
/// press, however close it comes back. Plain math, no WPF: the tests link this file.
/// </summary>
public sealed class TapTracker
{
    /// <summary>
    /// The touch slop, in device-independent pixels of the window (not stage units: a finger is as
    /// wide on a small screen): what Android (8 dp) and Windows (about 10) use.
    /// </summary>
    public const double Slop = 10;

    private double _x;
    private double _y;

    /// <summary>The pointer is down.</summary>
    public bool Pressed { get; private set; }

    /// <summary>This press (or the last one, until the next) went past the slop: not a tap.</summary>
    public bool Dragged { get; private set; }

    public void Press(double x, double y)
    {
        (_x, _y) = (x, y);
        Pressed = true;
        Dragged = false;
    }

    /// <summary>The pointer moved while down. True once, at the move that turns the press into a drag.</summary>
    public bool Move(double x, double y)
    {
        if (!Pressed || Dragged)
        {
            return false;
        }
        var (dx, dy) = (x - _x, y - _y);
        if (dx * dx + dy * dy <= Slop * Slop)
        {
            return false;
        }
        Dragged = true;
        return true;
    }

    /// <summary>The pointer is up. True when the press was a tap. <see cref="Dragged"/> stays readable until the next press.</summary>
    public bool Release()
    {
        var tap = Pressed && !Dragged;
        Pressed = false;
        return tap;
    }
}
