// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Tests;

/// <summary>mkw-recomp's own update check: launchers up to 1.4.6 kept it off; it is on by default again, once, then the player's.</summary>
public sealed class RecompUpdateCheckTests
{
    [Fact]
    public async Task The_update_check_older_launchers_kept_off_is_turned_back_on_once()
    {
        await using var rig = new Rig(new FakeSeatServer());
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        var ini = Path.Combine(rig.Game, "mkw.ini");
        File.WriteAllText(ini, "[launcher]\nupdate_check = 0\nmods = mkwmp\n");

        // not while the game runs: it holds mkw.ini
        rig.Target.RunningOverride = () => true;
        await rig.Launcher.FindGameAsync();
        Assert.Contains("update_check = 0", File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.False(rig.Launcher.Settings.RecompUpdateCheckRestored);

        rig.Target.RunningOverride = () => false;
        await rig.Launcher.FindGameAsync();
        Assert.DoesNotContain("update_check", File.ReadAllText(ini), StringComparison.Ordinal); // back to mkw-recomp's default: on
        Assert.Contains("mods = mkwmp", File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.True(rig.Launcher.Settings.RecompUpdateCheckRestored);
        Assert.True(ClientConfig.Load(rig.ConfigPath).Settings.RecompUpdateCheckRestored); // kept in client.toml

        // the player turns it off afterwards: that stays
        File.WriteAllText(ini, "[launcher]\nupdate_check = 0\nmods = mkwmp\n");
        await rig.Launcher.FindGameAsync();
        Assert.Contains("update_check = 0", File.ReadAllText(ini), StringComparison.Ordinal);
    }
}