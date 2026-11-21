// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Overlay;

namespace Mkwmp.Core.Tests;

/// <summary>When the game's window steps aside for the overlay (a Linux desktop under Wine), and when it was kept in front.</summary>
public class OverlayFocusTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("0")]
    public void Never_on_Windows(string? env) => Assert.False(OverlayFocus.StepGameAside(wine: false, gameMode: false, env));

    [Fact]
    public void On_a_Linux_desktop_under_Wine_by_default() => Assert.True(OverlayFocus.StepGameAside(wine: true, gameMode: false, null));

    [Fact]
    public void Not_in_gamescope_by_default() => Assert.False(OverlayFocus.StepGameAside(wine: true, gameMode: true, null));

    [Theory]
    [InlineData("0", false, false)]
    [InlineData("off", false, false)]
    [InlineData(" No ", false, false)]
    [InlineData("1", true, true)]
    [InlineData("on", false, true)]
    [InlineData("TRUE", true, true)]
    [InlineData("", false, true)]
    [InlineData("maybe", true, false)]
    public void The_player_s_override_wins(string env, bool gameMode, bool want) =>
        Assert.Equal(want, OverlayFocus.StepGameAside(wine: true, gameMode, env));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void Kept_behind_when_the_game_is_in_front_or_ours_is_not_active(bool active, bool gameForeground, bool want) =>
        Assert.Equal(want, OverlayFocus.KeptBehind(active, gameForeground));

    [Fact]
    public void The_steps_come_in_order()
    {
        Assert.True(OverlayFocus.StepAsideDelay < OverlayFocus.CheckAfter);
        Assert.True(OverlayFocus.RestoreWait + OverlayFocus.RestoreLead < TimeSpan.FromSeconds(1));
    }
}
