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
/// The controller in the launcher's XInput slot, as the game's SDL3 identifies the same device:
/// its USB ids (XInputGetCapabilitiesEx, the call SDL makes) and, for Steam Input's virtual pad
/// (28de:11ff), the real controller's type from Steam's <c>SteamVirtualGamepadInfo</c> file (SDL's
/// names: "switchpro", "joyconpair", "ps5", "xboxone"...). <see cref="Unknown"/> when the ids could
/// not be read: SDL then takes it for an Xbox 360 controller, and so does the launcher.
/// </summary>
public readonly record struct PadIdentity(ushort VendorId, ushort ProductId, string? SteamType = null)
{
    public static PadIdentity Unknown => default;

    /// <summary>Steam Input's virtual pad on Windows (SDL_IsJoystickSteamVirtualGamepad). Under Proton it has other ids (28de:12fd).</summary>
    public bool IsSteamVirtual => VendorId == PadLayout.ValveVendor && ProductId == PadLayout.SteamVirtualProduct;

    public override string ToString() =>
        $"{VendorId:x4}:{ProductId:x4}" + (SteamType is { Length: > 0 } ? $" (Steam: {SteamType})" : "");

    /// <summary>"vid:pid" or "vid:pid:steamtype", hexadecimal ids (the harness' <c>--pad-device</c>).</summary>
    public static bool TryParse(string? text, out PadIdentity pad)
    {
        pad = Unknown;
        var parts = (text ?? "").Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3
            || !ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var vid)
            || !ushort.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            return false;
        }
        pad = new PadIdentity(vid, pid, parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null);
        return true;
    }
}

/// <summary>
/// How the launcher reads its controller so that it does what the game does: the layout in effect
/// (the setting, with "auto" decided as the game decides it) and whether Steam's face buttons are
/// swapped back to their places first (as SDL does for a Nintendo controller under Steam Input).
/// </summary>
public readonly record struct PadMapping(FaceLayout Layout, bool SwapFaceButtons)
{
    public override string ToString() => SwapFaceButtons ? $"{Layout}, Steam's Nintendo buttons swapped back" : Layout.ToString();
}

/// <summary>
/// The one place where a physical face button becomes a role, so the whole window (and the hint
/// bar's letters, which name roles) does what the game does with the same press. The rule, the
/// game's (mkw-recomp input_map.c, SDL3 positional buttons): Nintendo makes the right (east) button A,
/// Xbox the bottom (south) one; Automatic is Nintendo for a controller whose right button is labelled
/// A (Switch Pro Controller, Joy-Con) and Xbox for every other. XInput names the buttons by their
/// Xbox labels: its A is the south button, B east, X west, Y north, except on Steam Input's virtual pad
/// for a Nintendo controller, where Steam ("Use Nintendo Button Layout", its default) sends the
/// button labelled A, the east one, as XInput's A: SDL swaps those back, and so does
/// <see cref="Position"/>. The other buttons (shoulders, Start, Back, the D-pad) are the same in
/// every layout. Plain code, no WPF: the tests link this file.
/// </summary>
public static class PadLayout
{
    /// <summary>The game setting that holds the layout ("auto", "nintendo", "xbox").</summary>
    public const string SettingId = "pad_layout";

    public const ushort ValveVendor = 0x28DE;
    public const ushort SteamVirtualProduct = 0x11FF;
    public const ushort NintendoVendor = 0x057E;

    /// <summary>The environment variable with the path of Steam's virtual pad list (SDL_steam_virtual_gamepad.c).</summary>
    public const string SteamVirtualGamepadInfoVariable = "SteamVirtualGamepadInfo";

    /// <summary>
    /// The layout for the game setting's value with no controller known: "auto" is Xbox then, as
    /// it is in the game for any controller that is not labelled as a Nintendo one.
    /// </summary>
    public static FaceLayout FromSetting(string? value) => Resolve(value, PadIdentity.Unknown).Layout;

    /// <summary>What the game does with this controller and this setting (see the class).</summary>
    public static PadMapping Resolve(string? setting, PadIdentity pad)
    {
        var value = setting?.Trim();
        var layout = string.Equals(value, "nintendo", StringComparison.OrdinalIgnoreCase) ? FaceLayout.Nintendo
            : string.Equals(value, "xbox", StringComparison.OrdinalIgnoreCase) ? FaceLayout.Xbox
            : NintendoLabels(pad) ? FaceLayout.Nintendo : FaceLayout.Xbox;
        return new PadMapping(layout, SwapsFaceButtons(pad));
    }

    /// <summary>
    /// The controller's right button is labelled A (SDL_GetGamepadButtonLabel(EAST) == A, the game's
    /// "auto" test): a Switch Pro Controller or Joy-Con, by Steam's type for its virtual pad, else by
    /// Nintendo's vendor id (not the GameCube adapter or controller, whose A is at the bottom).
    /// </summary>
    public static bool NintendoLabels(PadIdentity pad)
    {
        if (pad.SteamType is { Length: > 0 } type)
        {
            return IsNintendoType(type);
        }
        return pad.VendorId == NintendoVendor && pad.ProductId is not (0x0337 or 0x2073);
    }

    /// <summary>SDL's ShouldSwapFaceButtons: Steam's virtual pad standing for a Switch Pro Controller or Joy-Con.</summary>
    public static bool SwapsFaceButtons(PadIdentity pad) => pad.IsSteamVirtual && IsNintendoType(pad.SteamType);

    private static bool IsNintendoType(string? type) => type?.Trim().TrimStart('+', '-').ToLowerInvariant() is
        "switchpro" or "joyconleft" or "joyconright" or "joyconpair";

    /// <summary>Where the XInput button <paramref name="xinput"/> is on the controller (named as <see cref="FromPosition"/> names positions).</summary>
    public static PadButton Position(PadButton xinput, bool swapFaceButtons) => !swapFaceButtons ? xinput : xinput switch
    {
        PadButton.A => PadButton.B,
        PadButton.B => PadButton.A,
        PadButton.X => PadButton.Y,
        PadButton.Y => PadButton.X,
        _ => xinput,
    };

    /// <summary>The role of the XInput button <paramref name="xinput"/> with <paramref name="mapping"/>.</summary>
    public static PadButton ToRole(PadButton xinput, PadMapping mapping) => ToRole(Position(xinput, mapping.SwapFaceButtons), mapping.Layout);

    /// <summary>The role of the button at <paramref name="position"/> (south A, east B, west X, north Y) in <paramref name="layout"/>.</summary>
    public static PadButton ToRole(PadButton position, FaceLayout layout) => layout != FaceLayout.Nintendo ? position : position switch
    {
        PadButton.B => PadButton.A, // east confirms
        PadButton.A => PadButton.B, // south goes back
        PadButton.Y => PadButton.X, // north
        PadButton.X => PadButton.Y, // west
        _ => position,
    };

    /// <summary>A face button by where it is ("south", "east", "west", "north"), named as an Xbox controller labels that place; null for anything else.</summary>
    public static PadButton? FromPosition(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "SOUTH" => PadButton.A,
        "EAST" => PadButton.B,
        "WEST" => PadButton.X,
        "NORTH" => PadButton.Y,
        _ => null,
    };

    /// <summary>
    /// The type of Steam's virtual pad <paramref name="slot"/> in the text of its <c>SteamVirtualGamepadInfo</c> file
    /// (<c>[slot N]</c> sections of <c>key=value</c> lines, as SDL_UpdateSteamVirtualGamepadInfo reads them); null when absent.
    /// </summary>
    public static string? SteamTypeOf(string? info, int slot)
    {
        if (string.IsNullOrEmpty(info) || slot < 0)
        {
            return null;
        }
        var current = -1;
        string? type = null;
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim('\r', '\0');
            if (line.StartsWith("[slot ", StringComparison.Ordinal) && line.EndsWith(']')
                && int.TryParse(line.AsSpan(6, line.Length - 7).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (current == slot)
                {
                    break;
                }
                current = n;
                type = null;
            }
            else if (current == slot && line.StartsWith("type=", StringComparison.Ordinal))
            {
                type = line[5..];
            }
        }
        return current == slot && type is { Length: > 0 } ? type : null;
    }
}
