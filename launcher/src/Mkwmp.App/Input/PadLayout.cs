// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.Input;

/// <summary>
/// What a controller (or its keyboard equivalent) can ask the shell for. The face buttons are
/// roles, by the letters the hint bar shows: A confirms, B goes back, X and Y are a page's own.
/// Which physical button gives which role is <see cref="PadLayout"/>'s.
/// </summary>
public enum PadButton { A, B, X, Y, LB, RB, Start, Back, Up, Down, Left, Right }

/// <summary>Where A is on the controller (the game's Settings › Controls › Button layout).</summary>
public enum FaceLayout
{
    /// <summary>A at the bottom (south), B on the right: as labelled on an Xbox controller.</summary>
    Xbox,

    /// <summary>A on the right (east), B at the bottom: as on a Switch.</summary>
    Nintendo,
}

/// <summary>
/// The one place where a physical face button becomes a role, so the whole window (and the hint
/// bar's letters, which name roles) follows the button layout the player chose for the game.
/// XInput names the buttons by their Xbox labels: its A is the south button, B east, X west, Y north.
/// The other buttons (shoulders, Start, Back, the D-pad) are the same in both layouts. Plain code,
/// no WPF: the tests link this file.
/// </summary>
public static class PadLayout
{
    /// <summary>The game setting that holds the layout ("auto", "nintendo", "xbox").</summary>
    public const string SettingId = "pad_layout";

    /// <summary>
    /// The layout for the game setting's value. "auto" is Xbox: the game picks Nintendo only for a
    /// controller with Nintendo labels, and XInput (all the launcher reads) does not say which labels a controller has.
    /// </summary>
    public static FaceLayout FromSetting(string? value) =>
        string.Equals(value?.Trim(), "nintendo", StringComparison.OrdinalIgnoreCase) ? FaceLayout.Nintendo : FaceLayout.Xbox;

    /// <summary>The role of the XInput button <paramref name="physical"/> (named by its Xbox label) in <paramref name="layout"/>.</summary>
    public static PadButton ToRole(PadButton physical, FaceLayout layout) => layout != FaceLayout.Nintendo ? physical : physical switch
    {
        PadButton.B => PadButton.A, // east confirms
        PadButton.A => PadButton.B, // south goes back
        PadButton.Y => PadButton.X, // north
        PadButton.X => PadButton.Y, // west
        _ => physical,
    };

    /// <summary>A face button by where it is ("south", "east", "west", "north"), as XInput names it; null for anything else.</summary>
    public static PadButton? FromPosition(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "SOUTH" => PadButton.A,
        "EAST" => PadButton.B,
        "WEST" => PadButton.X,
        "NORTH" => PadButton.Y,
        _ => null,
    };
}
