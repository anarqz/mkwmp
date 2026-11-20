// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Platform;

namespace Mkwmp.App.Desktop;

/// <summary>Where the launcher runs: the Steam Deck's Game Mode has no tray; Wine / Proton has no reliable tray nor Windows notifications.</summary>
public static class DesktopEnvironment
{
    /// <summary>
    /// Steam's Game Mode (gamescope): one full-screen window, no tray, no desktop. The Rust
    /// launcher's test: <c>SteamGamepadUI</c>, <c>GAMESCOPE_WAYLAND_DISPLAY</c>, or
    /// <c>XDG_CURRENT_DESKTOP=gamescope</c> (Wine passes the Linux environment on).
    /// </summary>
    public static bool IsGameMode =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGamepadUI"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
        || string.Equals(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);

    /// <summary>Running under Wine / Proton (<see cref="WineHost.Detected"/>).</summary>
    public static bool IsWine => WineHost.Detected;

    /// <summary>A real Windows tray to hide into: not in Game Mode, not under Wine (its tray may not show, and a hidden window would be lost).</summary>
    public static bool HasTray => !IsGameMode && !IsWine;

    /// <summary>Without a tray the window can still step aside: minimized, on a Linux desktop under Wine.</summary>
    public static bool CanMinimize => !IsGameMode;
}
