// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

public class RecompLaunchEnvTests
{
    private static readonly Dictionary<string, string> NoIni = new(StringComparer.Ordinal);

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    private static string Names(IReadOnlyList<(string Name, string? Value, string Why)> list) =>
        string.Join(' ', list.Select(e => $"{e.Name}={e.Value}"));

    /// <summary>What Steam gave the MKWMP shortcut on SteamOS in Desktop Mode (ROG Xbox Ally X, 2026-11-21), in part.</summary>
    private static readonly (string, string)[] SteamOsDesktop =
    [
        ("SteamOS", "1"), ("SteamGameId", "15897112261177638912"), ("SteamAppId", "3701334880"), ("XDG_CURRENT_DESKTOP", "KDE"),
        ("SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD", "1"), ("SDL_GAMECONTROLLER_IGNORE_DEVICES", "0x28de/0x12fd,0x0b05/0x1b4c"),
    ];

    [Fact]
    public void Windows_gets_nothing()
    {
        Assert.Empty(RecompLaunchEnv.For(false, Env(SteamOsDesktop), NoIni));
        Assert.Empty(RecompLaunchEnv.For(false, Env(("SteamGameId", "1")), NoIni));
        Assert.False(RecompLaunchEnv.ForcesFullscreen(false, Env(SteamOsDesktop)));
    }

    [Fact]
    public void SteamOS_is_always_full_screen_in_desktop_mode_too()
    {
        var ini = new Dictionary<string, string>(StringComparer.Ordinal) { ["FULLSCREEN"] = "0" };
        Assert.Equal("MKW_FULLSCREEN=1", Names(RecompLaunchEnv.For(true, Env(SteamOsDesktop), ini)));
        Assert.True(RecompLaunchEnv.ForcesFullscreen(true, Env(SteamOsDesktop)));
    }

    [Theory]
    [InlineData("SteamGamepadUI", "1")]
    [InlineData("GAMESCOPE_WAYLAND_DISPLAY", "gamescope-0")]
    [InlineData("XDG_CURRENT_DESKTOP", "gamescope")]
    [InlineData("SteamDeck", "1")]
    public void Game_mode_and_gamescope_are_full_screen(string name, string value)
    {
        var ini = new Dictionary<string, string>(StringComparer.Ordinal) { ["FULLSCREEN"] = "0" };
        Assert.Equal("MKW_FULLSCREEN=1", Names(RecompLaunchEnv.For(true, Env((name, value)), ini)));
        Assert.True(RecompLaunchEnv.ForcesFullscreen(true, Env((name, value))));
    }

    [Fact]
    public void A_linux_desktop_is_full_screen_until_mkw_ini_says_otherwise()
    {
        Assert.Equal("MKW_FULLSCREEN=1", Names(RecompLaunchEnv.For(true, Env(("XDG_CURRENT_DESKTOP", "KDE")), NoIni)));
        var windowed = new Dictionary<string, string>(StringComparer.Ordinal) { ["FULLSCREEN"] = "0" };
        Assert.Empty(RecompLaunchEnv.For(true, Env(("XDG_CURRENT_DESKTOP", "KDE")), windowed));
        Assert.False(RecompLaunchEnv.ForcesFullscreen(true, Env(("XDG_CURRENT_DESKTOP", "KDE"))));
    }

    [Fact]
    public void The_players_own_MKW_FULLSCREEN_wins()
    {
        var env = Env([.. SteamOsDesktop, ("MKW_FULLSCREEN", "0")]);
        Assert.Empty(RecompLaunchEnv.For(true, env, NoIni));
        Assert.False(RecompLaunchEnv.ForcesFullscreen(true, env));
    }

    [Fact]
    public void From_steam_the_virtual_pad_is_allowed()
    {
        // Steam set it: kept as it is, nothing added
        Assert.DoesNotContain("SDL_", Names(RecompLaunchEnv.For(true, Env(SteamOsDesktop), NoIni)), StringComparison.Ordinal);
        // missing while started from Steam: set
        var noAllow = Env(("SteamGameId", "15897112261177638912"), ("SDL_GAMECONTROLLER_IGNORE_DEVICES", "0x28de/0x12fd"));
        Assert.Contains("SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1", Names(RecompLaunchEnv.For(true, noAllow, NoIni)), StringComparison.Ordinal);
        // not from Steam: no Steam Input pad to allow
        Assert.DoesNotContain("SDL_", Names(RecompLaunchEnv.For(true, Env(("XDG_CURRENT_DESKTOP", "KDE")), NoIni)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_page_follows()
    {
        var d = Rig.NewFolder("launchenv");
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestModExe);
        var install = new MkwRecompTarget("1.4.0") { RunningOverride = () => false }.Detect(d)!;
        var steamOs = RecompSettings.Schema(install, Env(SteamOsDesktop), underWine: true)!;
        Assert.Equal("1", steamOs.Find("fullscreen")!.Default);
        Assert.NotNull(steamOs.Find("fullscreen")!.Locked);
        var linux = RecompSettings.Schema(install, Env(("XDG_CURRENT_DESKTOP", "KDE")), underWine: true)!;
        Assert.Equal("1", linux.Find("fullscreen")!.Default);
        Assert.Null(linux.Find("fullscreen")!.Locked);
        var windows = RecompSettings.Schema(install, Env(SteamOsDesktop), underWine: false)!;
        Assert.Equal("0", windows.Find("fullscreen")!.Default);
        Assert.Null(windows.Find("fullscreen")!.Locked);
    }
}
