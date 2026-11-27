// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>Settings › Support › Debug mode: the mod's deep trace for the games PLAY starts, off by default.</summary>
public sealed class DebugModeTests
{
    [Fact]
    public void It_is_off_by_default_and_saved_only_while_on()
    {
        Assert.False(ClientConfig.Parse("").Settings.DebugMode);
        Assert.DoesNotContain("debug_mode", new ClientConfig().ToToml(), StringComparison.Ordinal);
        var on = new ClientConfig { Settings = new LauncherSettings { DebugMode = true } };
        Assert.Contains("\ndebug_mode = true\n", on.ToToml(), StringComparison.Ordinal);
        Assert.True(ClientConfig.Parse(on.ToToml()).Settings.DebugMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_game_gets_the_trace_only_in_debug_mode(bool debug)
    {
        await using var rig = new Rig(new FakeSeatServer());
        ProcessStartInfo? started = null;
        rig.Target.LaunchOverride = s => started = s;
        var install = rig.Target.Detect(rig.Game);
        Assert.NotNull(install);

        Assert.Null(rig.Target.Launch(install, new LaunchOptions(null, debug)));

        Assert.NotNull(started);
        Assert.Equal("--play", started.Arguments);
        if (debug)
        {
            Assert.Equal("1", started.Environment[MkwRecompTarget.DebugTrace]);
        }
        else
        {
            // nothing added: whatever the launcher's own environment has (a developer's shell) is left as it is
            Assert.Equal(Environment.GetEnvironmentVariable(MkwRecompTarget.DebugTrace), started.Environment.TryGetValue(MkwRecompTarget.DebugTrace, out var v) ? v : null);
        }
    }

    [Fact]
    public async Task A_bug_report_says_debug_mode_was_on()
    {
        await using var rig = new Rig(new FakeSeatServer());
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { DebugMode = true });
        var zip = await rig.Launcher.SaveBugReportAsync();
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry(SupportBundle.SystemFile)!.Open());
        Assert.Contains(", debug mode on", await reader.ReadLineAsync(), StringComparison.Ordinal);
    }
}
