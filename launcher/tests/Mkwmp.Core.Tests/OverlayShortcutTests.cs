// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;
using Mkwmp.Core.Overlay;

namespace Mkwmp.Core.Tests;

/// <summary>The overlay's controller shortcut: its names, client.toml, the state the mod reads, and the launcher's own matcher.</summary>
public class OverlayShortcutTests
{
    private const string Auth = "Bearer t0ken";

    private sealed class Host : ILocalApiHost
    {
        public string Token => "t0ken";
        public SeatSession? Seat => null;
        public IReadOnlyList<Friend>? FriendsOrNull => null;
        public ClientConfig Config { get; } = new();

        public string? EditVoice(Func<ClientConfig, ClientConfig> edit) => null;
    }

    [Fact]
    public void Every_choice_has_the_mod_s_name_and_reads_back()
    {
        // the names of native/probe/src/overlay.c kCombos, in the same order
        Assert.Equal(["guide_or_select_start", "guide", "select_start", "select_hold", "start_hold", "l3_r3", "lb_rb_start", "select_lb", "select_rb"],
            OverlayShortcuts.All.Select(s => s.WireName()));
        foreach (var s in OverlayShortcuts.All)
        {
            Assert.Equal(s, OverlayShortcuts.Parse(s.WireName()));
        }
        Assert.Equal(OverlayShortcut.GuideOrSelectStart, OverlayShortcuts.Parse(""));
        Assert.Equal(OverlayShortcut.GuideOrSelectStart, OverlayShortcuts.Parse(null));
        Assert.Equal(OverlayShortcut.GuideOrSelectStart, OverlayShortcuts.Parse("a_newer_one"));
        Assert.Equal(OverlayShortcut.SticksPressed, OverlayShortcuts.Parse(" L3_R3 "));
        Assert.True(OverlayShortcut.Guide.UsesGuide());
        Assert.False(OverlayShortcut.SelectHold.UsesGuide());
    }

    [Fact]
    public void Client_toml_keeps_the_choice_and_an_old_file_has_the_default()
    {
        Assert.Equal(OverlayShortcut.GuideOrSelectStart, ClientConfig.Parse("[ui]\ntheme = \"dark\"\n").Settings.Ui.OverlayShortcut);
        var config = new ClientConfig { Settings = new LauncherSettings { Ui = new UiSettings { OverlayShortcut = OverlayShortcut.BumpersStart } } };
        var text = config.ToToml();
        Assert.Contains("overlay_shortcut = \"lb_rb_start\"", text, StringComparison.Ordinal);
        Assert.Equal(OverlayShortcut.BumpersStart, ClientConfig.Parse(text).Settings.Ui.OverlayShortcut);
        Assert.Equal(OverlayShortcut.GuideOrSelectStart, ClientConfig.Parse("[ui]\noverlay_shortcut = \"nope\"\n").Settings.Ui.OverlayShortcut);
    }

    [Fact]
    public async Task The_state_tells_the_mod_a_shortcut_that_is_not_the_default()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        api.Overlay.Requested += (_, _) => { };
        api.Overlay.Enabled = true;
        var state = await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default);
        Assert.Equal("""{"open":false,"seq":0,"enabled":true}""", state.Body); // the default: as before
        api.Overlay.Shortcut = OverlayShortcut.SelectHold; // changed in Settings: the next poll has it
        state = await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default);
        Assert.Equal("""{"open":false,"seq":0,"enabled":true,"shortcut":"select_hold"}""", state.Body);
        var opened = await api.HandleAsync("POST", "/local/v1/overlay/open", "by=hold&pid=1", Auth, default, default);
        Assert.Equal("""{"open":true,"seq":1,"enabled":true,"shortcut":"select_hold"}""", opened.Body);
        api.Overlay.ReturnToGame(OverlayReturn.Menu);
        state = await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default);
        Assert.Equal("""{"open":false,"seq":2,"enabled":true,"action":"menu","shortcut":"select_hold"}""", state.Body);
    }

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    /// <summary>The readings, one per tick from 0; the ticks at which the matcher fired.</summary>
    private static List<int> Run(OverlayShortcut shortcut, params ushort[] readings)
    {
        var m = new OverlayShortcutMatcher();
        m.Use(shortcut);
        var fired = new List<int>();
        for (var i = 0; i < readings.Length; i++)
        {
            if (m.Step(readings[i], Tick * i))
            {
                fired.Add(i);
            }
        }
        return fired;
    }

    private const ushort Start = OverlayShortcutMatcher.Start, Back = OverlayShortcutMatcher.Back, Guide = OverlayShortcutMatcher.GuideButton;
    private const ushort L3 = OverlayShortcutMatcher.LeftThumb, R3 = OverlayShortcutMatcher.RightThumb;
    private const ushort Lb = OverlayShortcutMatcher.LeftShoulder, Rb = OverlayShortcutMatcher.RightShoulder;

    [Fact]
    public void The_launcher_s_matcher_fires_once_per_press_of_the_combination()
    {
        Assert.Equal([1, 4], Run(OverlayShortcut.Guide, 0, Guide, Guide, 0, Guide));
        Assert.Empty(Run(OverlayShortcut.Guide, Guide, Guide)); // held at the start: not a press
        // the default is Guide only here: Select + Start is mkw-recomp's own menu, which the launcher cannot hold back
        Assert.Equal([2], Run(OverlayShortcut.GuideOrSelectStart, 0, Back | Start, Guide));
        Assert.Equal([2], Run(OverlayShortcut.SelectStart, 0, Back, Back | Start, Back | Start, Start));
        Assert.Equal([2], Run(OverlayShortcut.SticksPressed, 0, L3, L3 | R3, L3 | R3, 0));
        Assert.Empty(Run(OverlayShortcut.SticksPressed, 0, L3, 0, R3, 0));
        Assert.Equal([2], Run(OverlayShortcut.BumpersStart, 0, Lb | Rb, Lb | Rb | Start, Lb | Rb | Start));
        Assert.Empty(Run(OverlayShortcut.BumpersStart, 0, Lb | Start, Rb | Start));
        Assert.Equal([1], Run(OverlayShortcut.SelectLb, 0, Back | Lb, Back | Lb | Rb));
        Assert.Equal([2], Run(OverlayShortcut.SelectRb, 0, Back | Lb, Back | Rb));
    }

    [Fact]
    public void The_launcher_s_matcher_times_a_hold()
    {
        var hold = (int)Math.Ceiling(OverlayShortcuts.HoldTime / Tick);
        var readings = Enumerable.Repeat(Back, hold + 10).Prepend((ushort)0).ToArray();
        Assert.Equal([1 + hold], Run(OverlayShortcut.SelectHold, readings)); // once, a second after the press
        Assert.Empty(Run(OverlayShortcut.SelectHold, [0, .. Enumerable.Repeat(Back, hold - 2), 0, .. Enumerable.Repeat(Back, hold - 2)]));
        Assert.Equal([1 + hold], Run(OverlayShortcut.StartHold, [0, .. Enumerable.Repeat(Start, hold + 3)]));
        Assert.Empty(Run(OverlayShortcut.StartHold, [.. Enumerable.Repeat(Start, hold * 2)])); // held from the start
        // another choice while buttons are down: not a press of it
        var m = new OverlayShortcutMatcher();
        Assert.False(m.Step(0, TimeSpan.Zero));
        Assert.False(m.Step(L3 | R3, Tick));
        m.Use(OverlayShortcut.SticksPressed);
        Assert.False(m.Step(L3 | R3, Tick * 2));
        Assert.False(m.Step(0, Tick * 3));
        Assert.True(m.Step(L3 | R3, Tick * 4));
    }
}
