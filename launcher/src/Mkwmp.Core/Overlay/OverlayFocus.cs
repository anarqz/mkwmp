// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Overlay;

/// <summary>
/// How the launcher's window reaches the front over the game under Wine on a Linux desktop (KDE Plasma, GNOME: a
/// stacking window manager; launcher/docs/OVERLAY.md §4.1).
/// <para>
/// There the window manager, not Windows' rules, decides who is in front, and its focus-stealing prevention refuses
/// the launcher: a process the player has not touched since the game started asks for its window to be active. The
/// window is mapped behind the full-screen game (KWin: under the active window, "demands attention"), so the
/// suspended game looks frozen. What every such window manager does allow is the active window going away: the game's
/// window is minimised once ours is mapped, and the window manager gives the focus to the next window of its focus
/// chain, which is ours (mapped last, just behind the active one). On the way back the game is restored first, then
/// ours is hidden: the game is then next in the chain.
/// </para>
/// Never on Windows (its foreground rules already let the launcher in, <c>AllowSetForegroundWindow</c> of the mod),
/// never in gamescope (it shows the window mapped last, and minimising is not its notion).
/// </summary>
public static class OverlayFocus
{
    /// <summary>The player's override: <c>0</c> never minimises the game, <c>1</c> always does under Wine (also in gamescope).</summary>
    public const string EnvName = "MKWMP_OVERLAY_MINIMIZE_GAME";

    /// <summary>From our window's show to the game's minimise: the window manager has managed ours (mapped) by then.</summary>
    public static readonly TimeSpan StepAsideDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>When the launcher looks whether it really came to the front (for the log and the hint).</summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromMilliseconds(1500);

    /// <summary>The way back: at most this long for the game's window to be restored before ours is hidden.</summary>
    public static readonly TimeSpan RestoreWait = TimeSpan.FromMilliseconds(300);

    /// <summary>... and this much more, for the window manager to have the restored window before ours goes.</summary>
    public static readonly TimeSpan RestoreLead = TimeSpan.FromMilliseconds(60);

    /// <summary>Whether the game's window steps aside (is minimised) while the overlay is up.</summary>
    /// <param name="wine">Under Wine / Proton.</param>
    /// <param name="gameMode">Steam's Game Mode (gamescope).</param>
    /// <param name="env">The value of <see cref="EnvName"/>, or null.</param>
    public static bool StepGameAside(bool wine, bool gameMode, string? env)
    {
        if (!wine)
        {
            return false;
        }
        switch (env?.Trim().ToLowerInvariant())
        {
            case "0" or "off" or "false" or "no":
                return false;
            case "1" or "on" or "true" or "yes":
                return true;
            default:
                return !gameMode;
        }
    }

    /// <summary>
    /// The check <see cref="CheckAfter"/> the show: the overlay did not come to the front when the game still is the
    /// foreground window, or ours is not the active one.
    /// </summary>
    public static bool KeptBehind(bool launcherActive, bool gameForeground) => gameForeground || !launcherActive;
}
