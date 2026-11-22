// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The environment the launcher gives <c>mkw.exe</c> on top of its own under Wine/Proton (docs/LINUX.md,
/// "Full screen and the controller"). On Windows nothing changes.
/// <para>
/// <b>Full screen.</b> mkw-recomp's Windows build takes its console defaults (cfg.c <c>DECK</c>,
/// <c>CONSOLE</c>: <c>fullscreen = 1</c>) only on a Steam Deck (<c>SteamDeck=1</c> or Valve's board) or when
/// Steam's Big Picture or gamescope is in its environment. The ROG Xbox Ally X with SteamOS is neither in
/// Desktop Mode, so the game opened in a 1920x1002 window (mkw.log, 2026-11-21). <c>MKW_FULLSCREEN</c> in the
/// game's environment wins over mkw.ini (cfg.c <c>raw_get</c>):
/// </para>
/// <list type="bullet">
/// <item>On SteamOS or in a gamescope session it is always <c>1</c>: the handheld has no window to show, and Steam
/// (Game Mode) and the player's pad expect the game over the whole screen. The launcher's Settings › Game shows
/// the row locked.</item>
/// <item>Elsewhere under Wine (a Linux desktop) it is <c>1</c> only while mkw.ini has no <c>fullscreen</c> line:
/// the default, which the player changes in the game (F11 or its menu) or in the launcher.</item>
/// </list>
/// <para>
/// <b>The controller.</b> Steam Input's virtual pad reaches Proton as a Steam virtual gamepad (28de:12fd on the Ally
/// X, <c>SteamVirtualGamepadInfo</c>), and Steam's <c>SDL_GAMECONTROLLER_IGNORE_DEVICES</c> lists that id with the
/// physical pads. SDL 3.4 (SDL_gamepad.c <c>SDL_ShouldIgnoreGamepad</c>) skips the list under Wine only with
/// <c>SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1</c>, which Steam sets for its own launch: it is kept, and set
/// when it is missing while the launcher runs from Steam (<c>SteamGameId</c>), so the game never drops the pad the
/// player holds.
/// </para>
/// <para>
/// <b>Shadow storage.</b> Under Proton on NVIDIA (RTX 3070, driver 580.196.07, Proton 11, Debian 12; 21/11/2026) the
/// driver refused every host memory import (VK_ERROR_OUT_OF_HOST_MEMORY, 499 of 499): Wine's write watch takes the
/// write permission off the guest heap's pages, so they cannot be pinned, and the run ended in "render target view
/// could not be created". mkw-recomp's Windows build imports by default and takes shadow storage (host-visible copies,
/// Linux's default) only with <c>MKW_SHADOW=1</c> (0.1.17 runtime/gfx/vk/mem/vk_mem_log.c, <c>_WIN32</c> branch;
/// the environment wins over mkw.ini, cfg.c <c>raw_get</c>). Our fork's unreleased fix/wine-shadow-default made it the
/// default under Wine; with the official build the launcher sets it, unless the player set <c>MKW_SHADOW</c> in the
/// environment or <c>shadow</c> in mkw.ini (0 brings the imports back).
/// </para>
/// </summary>
public static class RecompLaunchEnv
{
    public const string Fullscreen = "MKW_FULLSCREEN";
    public const string AllowSteamVirtualGamepad = "SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD";
    public const string Shadow = "MKW_SHADOW";

    /// <summary>SteamOS (Steam sets <c>SteamOS=1</c> there) or a gamescope session (Game Mode, or gamescope run by hand).</summary>
    public static bool SteamOsOrGamescope(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return env("SteamOS") == "1" || env("SteamDeck") == "1" || env("SteamGamepadUI") == "1"
            || env("GAMESCOPE_WAYLAND_DISPLAY") is { Length: > 0 }
            || string.Equals(env("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The launcher keeps the game full screen: under Wine, on SteamOS or in gamescope, unless the player set <c>MKW_FULLSCREEN</c> themselves.</summary>
    public static bool ForcesFullscreen(bool wine, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return wine && env(Fullscreen) is null && SteamOsOrGamescope(env);
    }

    /// <summary>
    /// The variables to set for the game (value null: remove), with why, for the log. <paramref name="ini"/>: what mkw.ini
    /// sets (the game's key names, upper case).
    /// </summary>
    public static IReadOnlyList<(string Name, string? Value, string Why)> For(bool wine, Func<string, string?> env, IReadOnlyDictionary<string, string> ini)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(ini);
        var list = new List<(string, string?, string)>();
        if (!wine)
        {
            return list;
        }
        if (env(Fullscreen) is null)
        {
            if (SteamOsOrGamescope(env))
            {
                list.Add((Fullscreen, "1", "SteamOS / gamescope: always full screen"));
            }
            else if (!ini.ContainsKey("FULLSCREEN"))
            {
                list.Add((Fullscreen, "1", "Wine: full screen until mkw.ini says otherwise"));
            }
        }
        if (env(Shadow) is null && !ini.ContainsKey("SHADOW"))
        {
            list.Add((Shadow, "1", "Wine: shadow storage, as on Linux (NVIDIA under Proton refuses the host memory imports)"));
        }
        if (env("SteamGameId") is { Length: > 0 } && env(AllowSteamVirtualGamepad) != "1")
        {
            list.Add((AllowSteamVirtualGamepad, "1", "started from Steam: Steam Input's virtual pad is the player's controller"));
        }
        return list;
    }
}
