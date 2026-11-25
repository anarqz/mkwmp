// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Tests;

/// <summary>mkw-recomp's update check and start screen: older launchers kept them (off; Skip); each goes back to its default once, then is the player's.</summary>
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

    [Fact]
    public async Task The_start_screen_older_launchers_kept_on_skip_goes_back_to_automatic_once_also_after_1_4_7()
    {
        // a 1.4.7 player: the update check was given back already, the start screen was still kept on Skip
        await using var rig = new Rig(new FakeSeatServer());
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { RecompUpdateCheckRestored = true });
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        var ini = Path.Combine(rig.Game, "mkw.ini");
        File.WriteAllText(ini, "[launcher]\nupdate_check = 0\nstart_screen = skip\nmods = mkwmp\n");

        await rig.Launcher.FindGameAsync();

        var text = File.ReadAllText(ini);
        Assert.DoesNotContain("start_screen", text, StringComparison.Ordinal); // back to mkw-recomp's default: Automatic, it does not skip
        Assert.Contains("update_check = 0", text, StringComparison.Ordinal);   // already given back once: this 0 is the player's
        Assert.True(rig.Launcher.Settings.RecompStartScreenRestored);
        Assert.True(ClientConfig.Load(rig.ConfigPath).Settings.RecompStartScreenRestored);
        Assert.Null(rig.Launcher.GameSettings.Schema?.Find("start_screen")?.Locked);

        // the player picks Skip afterwards: that stays
        File.WriteAllText(ini, "[launcher]\nstart_screen = skip\nmods = mkwmp\n");
        await rig.Launcher.FindGameAsync();
        Assert.Contains("start_screen = skip", File.ReadAllText(ini), StringComparison.Ordinal);
    }
}