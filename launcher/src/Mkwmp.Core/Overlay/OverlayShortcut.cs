// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Overlay;

/// <summary>
/// The controller shortcut that brings MKWMP over the game (Settings › Game; launcher/docs/OVERLAY.md §2). One entry is
/// one combination. Saved as <c>[ui] overlay_shortcut</c> in client.toml and sent to the mod in the overlay's state
/// (<c>"shortcut"</c>, docs/contracts/LAUNCHER_API.md), which takes it at its next poll: no game restart. The mod's
/// matcher is <c>overlay_gate</c> (native/probe/src/overlay.c), the launcher's own <see cref="OverlayShortcutMatcher"/>.
/// </summary>
public enum OverlayShortcut
{
    /// <summary>What the overlay always did: Guide, Select + Start, and Select held for a second as well.</summary>
    GuideOrSelectStart,

    /// <summary>The Guide (Xbox / PS / Home) button only.</summary>
    Guide,

    /// <summary>Select + Start together.</summary>
    SelectStart,

    /// <summary>Select held for a second (a short Select still reaches the game, on its release).</summary>
    SelectHold,

    /// <summary>Start held for a second (a short Start still pauses, on its release).</summary>
    StartHold,

    /// <summary>Both sticks pressed (L3 + R3); one alone stays the game's.</summary>
    SticksPressed,

    /// <summary>LB and RB held, then Start.</summary>
    BumpersStart,

    /// <summary>Select + LB.</summary>
    SelectLb,

    /// <summary>Select + RB.</summary>
    SelectRb,
}

public static class OverlayShortcuts
{
    /// <summary>A hold is this long (the mod's OVERLAY_HOLD_MS).</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);

    /// <summary>Every choice, in the picker's order (the default first).</summary>
    public static IReadOnlyList<OverlayShortcut> All { get; } = Enum.GetValues<OverlayShortcut>();

    /// <summary>The name in client.toml and on the wire (the mod's <c>overlay_combo_name</c>).</summary>
    public static string WireName(this OverlayShortcut shortcut) => shortcut switch
    {
        OverlayShortcut.Guide => "guide",
        OverlayShortcut.SelectStart => "select_start",
        OverlayShortcut.SelectHold => "select_hold",
        OverlayShortcut.StartHold => "start_hold",
        OverlayShortcut.SticksPressed => "l3_r3",
        OverlayShortcut.BumpersStart => "lb_rb_start",
        OverlayShortcut.SelectLb => "select_lb",
        OverlayShortcut.SelectRb => "select_rb",
        _ => "guide_or_select_start",
    };

    /// <summary>From <see cref="WireName"/>; anything else (an empty or newer value) is the default.</summary>
    public static OverlayShortcut Parse(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.WireName(), name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The Guide button is (part of) it: Steam and the Xbox Game Bar may take it first.</summary>
    public static bool UsesGuide(this OverlayShortcut shortcut) => shortcut is OverlayShortcut.GuideOrSelectStart or OverlayShortcut.Guide;
}

/// <summary>
/// The launcher's own reading of the chosen shortcut on XInput buttons (the Guide watch, for a game without the mod's
/// overlay). It only sees: it cannot keep the buttons from the game, as the mod's filter does. Pure; one call per poll.
/// </summary>
public sealed class OverlayShortcutMatcher
{
    // XINPUT_GAMEPAD_* (and 0x0400, the Guide bit of XInputGetStateEx)
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    public const ushort GuideButton = 0x0400;

    private bool _primed;
    private bool _down;
    private TimeSpan? _holdSince;
    private bool _holdFired = true; // what is held at the first reading is not a press

    public OverlayShortcut Shortcut { get; private set; }

    /// <summary>Another combination: what is held now is not a press of it.</summary>
    public void Use(OverlayShortcut shortcut)
    {
        if (shortcut != Shortcut)
        {
            Shortcut = shortcut;
            Reset();
        }
    }

    /// <summary>What is held at the next reading is not a press (the watch stopped, the window came back).</summary>
    public void Reset()
    {
        _primed = false;
        _holdSince = null;
        _holdFired = true;
    }

    /// <summary>
    /// One reading (<paramref name="buttons"/>: the XInput bits of every pad together) at <paramref name="now"/>:
    /// true once per press of the combination. For the default entry only Guide counts here: Select + Start is
    /// mkw-recomp's own menu shortcut, which the launcher cannot keep shut (launcher/docs/OVERLAY.md §2).
    /// </summary>
    public bool Step(ushort buttons, TimeSpan now)
    {
        var (all, hold) = Shortcut switch
        {
            OverlayShortcut.GuideOrSelectStart or OverlayShortcut.Guide => (GuideButton, (ushort)0),
            OverlayShortcut.SelectStart => ((ushort)(Back | Start), (ushort)0),
            OverlayShortcut.SelectHold => ((ushort)0, Back),
            OverlayShortcut.StartHold => ((ushort)0, Start),
            OverlayShortcut.SticksPressed => ((ushort)(LeftThumb | RightThumb), (ushort)0),
            OverlayShortcut.BumpersStart => ((ushort)(LeftShoulder | RightShoulder | Start), (ushort)0),
            OverlayShortcut.SelectLb => ((ushort)(Back | LeftShoulder), (ushort)0),
            OverlayShortcut.SelectRb => ((ushort)(Back | RightShoulder), (ushort)0),
            _ => (GuideButton, (ushort)0),
        };
        var pressed = false;
        if (all != 0)
        {
            var down = (buttons & all) == all;
            pressed = _primed && down && !_down;
            _down = down;
        }
        if (hold != 0)
        {
            if ((buttons & hold) != hold)
            {
                _holdSince = null;
                _holdFired = false;
            }
            else if (_primed && !_holdFired)
            {
                _holdSince ??= now;
                if (now - _holdSince.Value >= OverlayShortcuts.HoldTime)
                {
                    _holdFired = true;
                    pressed = true;
                }
            }
        }
        _primed = true;
        return pressed;
    }
}
