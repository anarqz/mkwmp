// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Tests;

/// <summary>What the launcher hands the MKWMP mod through its settings file (the mod has no settings page of its own).</summary>
public class ModSettingsSyncTests
{
    private static Dictionary<string, string> For(LauncherSettings s) =>
        Launcher.ModSettingsFor(s).ToDictionary(k => k.Key, k => k.Value);

    [Fact]
    public void A_signed_in_launcher_hands_over_the_account_voice_and_camera()
    {
        var s = new LauncherSettings
        {
            Server = "https://mkwmp.example", Name = "Bia", Pin = "123456", AdultConsent = true, Seat = SeatMode.Online,
            Voice = new VoiceSettings { Enabled = false, Input = "{in}", Output = "{out}", InputGain = 40, OutputGain = 250, Camera = "USB Cam", CameraMirror = false },
        };
        var m = For(s);
        Assert.Equal("1", m["consent18"]);
        Assert.Equal("https://mkwmp.example", m["server"]);
        Assert.Equal("Bia", m["pin_name"]);
        Assert.Equal("123456", m["pin"]);
        Assert.Equal("online", m["seat_mode"]);
        Assert.Equal("0", m["voice"]);
        Assert.Equal("{in}", m["voice_input"]);
        Assert.Equal("{out}", m["voice_output"]);
        Assert.Equal("40", m["voice_in_gain"]);
        Assert.Equal("100", m["voice_out_gain"]); // clamped to the mod's range
        Assert.Equal("USB Cam", m["camera_device"]);
        Assert.Equal("0", m["camera_mirror"]);
        Assert.False(m.ContainsKey("camera")); // the camera switches are the Voice & Camera pane's own writes
    }

    [Fact]
    public void A_launcher_without_an_account_leaves_the_account_alone()
    {
        var offline = For(new LauncherSettings());
        Assert.Equal(["camera_device", "camera_mirror"], offline.Keys.Order());
        var notRegistered = For(new LauncherSettings { Server = "https://mkwmp.example", Name = "Bia", AdultConsent = false });
        Assert.Equal("0", notRegistered["consent18"]);
        Assert.False(notRegistered.ContainsKey("pin") || notRegistered.ContainsKey("pin_name") || notRegistered.ContainsKey("server"));
    }
}
