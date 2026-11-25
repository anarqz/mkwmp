// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>Settings › Game, "Open mkw-recomp": mkw-recomp's own start screen, not the game.</summary>
public sealed class OwnLauncherTests
{
    [Fact]
    public async Task It_starts_mkw_exe_on_its_start_screen_whatever_mkw_ini_says()
    {
        await using var rig = new Rig(new FakeSeatServer());
        ProcessStartInfo? started = null;
        rig.Target.OwnLauncherOverride = s => started = s;
        Assert.True(rig.Target.Capabilities.HasFlag(TargetCapabilities.OwnLauncher));

        await rig.Launcher.OpenGameLauncherAsync();

        Assert.NotNull(started);
        Assert.Equal(Path.Combine(rig.Game, "mkw.exe"), started.FileName);
        Assert.Equal("--launcher", started.Arguments); // never --play: that goes straight into the game
        Assert.Equal(rig.Game, started.WorkingDirectory);
        // the launcher keeps start_screen = skip in mkw.ini; the environment wins over it, and these two would skip the screen
        Assert.Equal("always", started.Environment["MKW_START_SCREEN"]);
        Assert.False(started.Environment.ContainsKey("MKW_LAUNCHED"));
        Assert.False(started.Environment.ContainsKey("MKW_NO_LAUNCHER"));
        Assert.Equal((Strings.OwnLauncherOpened("mkw-recomp"), false), (rig.Launcher.Game.Note, rig.Launcher.Game.NoteIsError));
        Assert.False(rig.Started); // not a game session: no PLAY, no seat
    }

    [Fact]
    public async Task It_is_refused_while_the_game_runs_and_without_a_game_folder()
    {
        await using var rig = new Rig(new FakeSeatServer());
        var starts = 0;
        rig.Target.OwnLauncherOverride = _ => starts++;
        rig.Target.RunningOverride = () => true;

        await rig.Launcher.OpenGameLauncherAsync();
        Assert.Equal((Strings.RecompCloseGameFirst, true), (rig.Launcher.Game.Note, rig.Launcher.Game.NoteIsError));

        rig.Target.RunningOverride = () => false;
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Game = Path.Combine(rig.Root, "nowhere") });
        await rig.Launcher.OpenGameLauncherAsync();
        Assert.True(rig.Launcher.Game.NoteIsError);
        Assert.Equal(0, starts);
    }
}