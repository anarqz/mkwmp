// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

public class ModSettingsTests
{
    private static (IGameTarget Target, GameInstall Install, string File, Action<bool> Run) Game(bool withProfile = true)
    {
        var d = Rig.NewFolder("modsettings");
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestExe);
        var running = false;
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => running };
        var install = target.Detect(d)!;
        if (withProfile)
        {
            Directory.CreateDirectory(Path.Combine(d, "profile"));
        }
        return (target, install, Path.Combine(d, "profile", "mod-settings", "mkwmp.ini"), on => running = on);
    }

    [Fact]
    public void Reads_the_hosts_flat_file_and_only_one_means_on()
    {
        var (target, install, file, _) = Game();
        Assert.Equal(file, ModSettings.PathFor(install));
        Assert.Null(target.ReadModSetting(install, ModSettings.Camera)); // no file yet
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "consent18=1\npin=123456\ncamera=1\ncamera_device=USB Video Device\ncamera_view=0\n");
        Assert.Equal("1", target.ReadModSetting(install, ModSettings.Camera));
        Assert.Equal("0", target.ReadModSetting(install, ModSettings.CameraView));
        Assert.Equal("USB Video Device", target.ReadModSetting(install, "camera_device"));
        Assert.Null(target.ReadModSetting(install, "camera_mirror"));
        Assert.Null(target.ReadModSetting(install, "bad key"));
    }

    [Fact]
    public void Write_keeps_every_other_line_and_the_hosts_format()
    {
        var (target, install, file, _) = Game();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "consent18=1\npin=123456\ncamera=1\ncamera_device=USB Video Device\nreport_reason=harassment\n");
        Assert.True(target.WriteModSetting(install, ModSettings.Camera, "0"));
        Assert.True(target.WriteModSetting(install, ModSettings.CameraView, "1"));
        Assert.Equal("consent18=1\npin=123456\ncamera=0\ncamera_device=USB Video Device\nreport_reason=harassment\ncamera_view=1\n", File.ReadAllText(file));
        Assert.True(target.WriteModSetting(install, ModSettings.CameraView, null));
        Assert.Null(target.ReadModSetting(install, ModSettings.CameraView));
        Assert.DoesNotContain("camera_view", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.DoesNotContain('\r', File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.mkwmp-tmp"));
    }

    [Fact]
    public void Write_creates_the_file_in_a_player_folder_the_game_made()
    {
        var (target, install, file, _) = Game();
        Assert.True(target.WriteModSetting(install, ModSettings.Camera, "1"));
        Assert.Equal("camera=1\n", File.ReadAllText(file));
    }

    [Fact]
    public void Write_refuses_while_the_game_runs_or_never_ran()
    {
        var (target, install, file, run) = Game();
        run(true);
        Assert.Equal(ModSettings.GameIsRunning, Assert.Throws<LauncherException>(() => target.WriteModSetting(install, ModSettings.Camera, "1")).Message);
        Assert.False(File.Exists(file));
        run(false);
        Assert.Throws<LauncherException>(() => target.WriteModSetting(install, "bad key", "1"));
        Assert.Throws<LauncherException>(() => target.WriteModSetting(install, ModSettings.Camera, "1\ncamera_view=1"));

        var (bare, never, neverFile, _) = Game(withProfile: false);
        Assert.Equal(ModSettings.NoProfileYet, Assert.Throws<LauncherException>(() => bare.WriteModSetting(never, ModSettings.Camera, "1")).Message);
        Assert.False(File.Exists(neverFile));
    }

    [Fact]
    public void Another_target_has_no_mod_settings()
    {
        var (target, install, _, _) = Game();
        var other = install with { TargetId = "some-emulator" };
        Assert.Null(ModSettings.PathFor(other));
        Assert.Null(target.ReadModSetting(other, ModSettings.Camera));
        Assert.False(target.WriteModSetting(other, ModSettings.Camera, "1"));
    }
}
