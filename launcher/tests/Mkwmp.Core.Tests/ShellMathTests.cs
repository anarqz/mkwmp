// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Xml.Linq;
using Mkwmp.App.Input;
using Mkwmp.App.Themes;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The window's pure math, linked from Mkwmp.App (no WPF): the button layout, the scroll motion,
/// the contrast steps of the palette.
/// </summary>
public class ShellMathTests
{
    // ── the button layout ──

    [Fact]
    public void XboxLayoutKeepsXInputNames()
    {
        foreach (var button in Enum.GetValues<PadButton>())
        {
            Assert.Equal(button, PadLayout.ToRole(button, FaceLayout.Xbox));
        }
    }

    [Fact]
    public void SwitchLayoutConfirmsWithTheRightButton()
    {
        // XInput's B is the east (right) button: with Switch it is A, confirm; the south one goes back
        Assert.Equal(PadButton.A, PadLayout.ToRole(PadLayout.FromPosition("east")!.Value, FaceLayout.Switch));
        Assert.Equal(PadButton.B, PadLayout.ToRole(PadLayout.FromPosition("south")!.Value, FaceLayout.Switch));
        Assert.Equal(PadButton.X, PadLayout.ToRole(PadLayout.FromPosition("north")!.Value, FaceLayout.Switch));
        Assert.Equal(PadButton.Y, PadLayout.ToRole(PadLayout.FromPosition("west")!.Value, FaceLayout.Switch));
        // the rest is the same
        foreach (var button in new[] { PadButton.LB, PadButton.RB, PadButton.Start, PadButton.Back, PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right })
        {
            Assert.Equal(button, PadLayout.ToRole(button, FaceLayout.Switch));
        }
    }

    [Theory]
    [InlineData("nintendo", FaceLayout.Switch)]
    [InlineData("NINTENDO", FaceLayout.Switch)]
    [InlineData("xbox", FaceLayout.Xbox)]
    [InlineData("auto", FaceLayout.Xbox)]
    [InlineData("", FaceLayout.Xbox)]
    [InlineData(null, FaceLayout.Xbox)]
    public void LayoutFromTheGameSetting(string? value, FaceLayout expected) => Assert.Equal(expected, PadLayout.FromSetting(value));

    // The truth table (docs: launcher/docs/DESIGN.md, "Button layout"): for each controller as XInput and the game's
    // SDL3 see it, and each pad_layout value, the physical place (south, east) that confirms in the launcher. The game's
    // A is at the same place: input_map.c, Switch east A, Xbox south A, auto Switch when the east button is labelled A.
    // xinputA: where XInput's A button physically is on that controller.
    [Theory]
    // an Xbox controller on Windows (and the Steam Deck, the ROG Ally, any pad Proton shows the game: 28de:12fd)
    [InlineData("045e:0b13", "south", "auto", "south")]
    [InlineData("045e:0b13", "south", "xbox", "south")]
    [InlineData("045e:0b13", "south", "nintendo", "east")]
    [InlineData("28de:12fd", "south", "auto", "south")]
    [InlineData("28de:12fd", "south", "nintendo", "east")]
    // XInput gave no ids: SDL takes it for an Xbox 360 controller
    [InlineData("0000:0000", "south", "auto", "south")]
    [InlineData("0000:0000", "south", "nintendo", "east")]
    // a PlayStation controller through Steam Input on Windows: cross is XInput's A
    [InlineData("28de:11ff:ps5", "south", "auto", "south")]
    [InlineData("28de:11ff:ps5", "south", "xbox", "south")]
    [InlineData("28de:11ff:ps5", "south", "nintendo", "east")]
    // a Switch Pro Controller / Joy-Con through Steam Input on Windows: Steam sends the A label (east) as XInput's A
    [InlineData("28de:11ff:switchpro", "east", "auto", "east")]
    [InlineData("28de:11ff:switchpro", "east", "nintendo", "east")]
    [InlineData("28de:11ff:switchpro", "east", "xbox", "south")]
    [InlineData("28de:11ff:joyconpair", "east", "auto", "east")]
    [InlineData("28de:11ff:joyconpair", "east", "xbox", "south")]
    // a Switch controller XInput sees with its own ids (a Proton that passes them): labelled A on the right, no Steam swap
    [InlineData("057e:2009", "south", "auto", "east")]
    [InlineData("057e:2009", "south", "nintendo", "east")]
    [InlineData("057e:2009", "south", "xbox", "south")]
    // the GameCube adapter: A at the bottom
    [InlineData("057e:0337", "south", "auto", "south")]
    public void ConfirmIsWhereTheGamesAIs(string device, string xinputA, string setting, string confirms)
    {
        Assert.True(PadIdentity.TryParse(device, out var pad));
        var mapping = PadLayout.Resolve(setting, pad);
        // XInput's A is at xinputA, its B at the other place
        var atSouth = xinputA == "south" ? PadButton.A : PadButton.B;
        var atEast = xinputA == "south" ? PadButton.B : PadButton.A;
        Assert.Equal(PadButton.A, PadLayout.ToRole(confirms == "south" ? atSouth : atEast, mapping));
        Assert.Equal(PadButton.B, PadLayout.ToRole(confirms == "south" ? atEast : atSouth, mapping));
        // the game's A at that place, in the game's own terms (east A in its Switch layout, south A in Xbox)
        Assert.Equal(confirms == "east" ? FaceLayout.Switch : FaceLayout.Xbox, mapping.Layout);
        // the harness' --press east|south names places: the same roles
        Assert.Equal(PadButton.A, PadLayout.ToRole(PadLayout.FromPosition(confirms)!.Value, mapping.Layout));
    }

    [Theory]
    // Automatic: by the controller, as the game's prompts
    [InlineData("057e:2009", "auto", GlyphFamily.Nintendo)]      // Pro Controller through SDL
    [InlineData("28de:11ff:switchpro", null, GlyphFamily.Nintendo)]
    [InlineData("054c:0ce6", "auto", GlyphFamily.PlayStation)]   // DualSense through SDL
    [InlineData("28de:11ff:ps5", "auto", GlyphFamily.PlayStation)]
    [InlineData("28de:11ff:xboxone", "auto", GlyphFamily.Xbox)]
    [InlineData("045e:028e", "auto", GlyphFamily.Xbox)]
    [InlineData("057e:0337", "auto", GlyphFamily.Xbox)]          // GameCube adapter: not Switch labels
    [InlineData("0000:0000", "", GlyphFamily.Xbox)]
    // a chosen family is kept, whatever the controller
    [InlineData("057e:2009", "xbox", GlyphFamily.Xbox)]
    [InlineData("045e:028e", "nintendo", GlyphFamily.Nintendo)]
    [InlineData("045e:028e", "PlayStation", GlyphFamily.PlayStation)]
    [InlineData("057e:2009", "keyboard", GlyphFamily.Keyboard)]
    public void GlyphsFollowTheGamesButtonIcons(string device, string? setting, GlyphFamily family)
    {
        Assert.True(PadIdentity.TryParse(device, out var pad));
        Assert.Equal(family, PadLayout.Glyphs(setting, pad));
    }

    [Theory]
    // a Pro Controller in the Switch layout: confirm is the right button, labelled A; back the bottom one, B
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.A, "A")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.B, "B")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.X, "X")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.Y, "Y")]
    // a Pro Controller with the Xbox layout chosen: confirm is the bottom button, which it labels B
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Xbox, PadButton.A, "B")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Xbox, PadButton.B, "A")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.LB, "L")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.Start, "+")]
    [InlineData(GlyphFamily.Nintendo, FaceLayout.Switch, PadButton.Back, "−")]
    // an Xbox pad with the Switch layout chosen: confirm is the right button, which it labels B
    [InlineData(GlyphFamily.Xbox, FaceLayout.Switch, PadButton.A, "B")]
    [InlineData(GlyphFamily.Xbox, FaceLayout.Xbox, PadButton.A, "A")]
    [InlineData(GlyphFamily.Xbox, FaceLayout.Xbox, PadButton.RB, "RB")]
    [InlineData(GlyphFamily.Xbox, FaceLayout.Xbox, PadButton.Start, "")]   // drawn as a picture
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Xbox, PadButton.A, "cross")]
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Xbox, PadButton.B, "circle")]
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Switch, PadButton.A, "circle")]
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Xbox, PadButton.X, "square")]
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Xbox, PadButton.Y, "triangle")]
    [InlineData(GlyphFamily.PlayStation, FaceLayout.Xbox, PadButton.LB, "L1")]
    public void GlyphsLabelTheRolesButton(GlyphFamily family, FaceLayout layout, PadButton role, string label) =>
        Assert.Equal(label, PadLayout.Label(role, new GlyphLook(family, layout)));

    [Fact]
    public void SteamsSwitchSwapIsUndoneOnlyForItsVirtualPad()
    {
        Assert.True(PadLayout.SwapsFaceButtons(new PadIdentity(0x28DE, 0x11FF, "switchpro")));
        Assert.True(PadLayout.SwapsFaceButtons(new PadIdentity(0x28DE, 0x11FF, "joyconleft")));
        Assert.False(PadLayout.SwapsFaceButtons(new PadIdentity(0x28DE, 0x11FF, "xboxone")));
        Assert.False(PadLayout.SwapsFaceButtons(new PadIdentity(0x28DE, 0x11FF)));
        Assert.False(PadLayout.SwapsFaceButtons(new PadIdentity(0x28DE, 0x12FD, "switchpro"))); // Proton's: SDL can't tell either
        Assert.False(PadLayout.SwapsFaceButtons(new PadIdentity(0x057E, 0x2009)));
        // X and Y follow A and B
        var pro = PadLayout.Resolve("nintendo", new PadIdentity(0x28DE, 0x11FF, "switchpro"));
        Assert.Equal(PadButton.X, PadLayout.ToRole(PadButton.X, pro)); // XInput X = Steam's label X = north: Switch X
        Assert.Equal(PadButton.Y, PadLayout.ToRole(PadButton.Y, pro));
        Assert.Equal(PadButton.Start, PadLayout.ToRole(PadButton.Start, pro));
    }

    [Fact]
    public void SteamVirtualGamepadInfoGivesTheSlotsType()
    {
        const string info = "[slot 0]\nhandle=123\nVID=0x45e\nPID=0x28e\ntype=ps5\n[slot 1]\r\nname=Pro Controller\r\ntype=switchpro\r\n[slot 2]\nhandle=9\n";
        Assert.Equal("ps5", PadLayout.SteamTypeOf(info, 0));
        Assert.Equal("switchpro", PadLayout.SteamTypeOf(info, 1));
        Assert.Null(PadLayout.SteamTypeOf(info, 2));
        Assert.Null(PadLayout.SteamTypeOf(info, 3));
        Assert.Null(PadLayout.SteamTypeOf(null, 0));
        Assert.False(PadIdentity.TryParse("28de", out _));
        Assert.True(PadIdentity.TryParse("28de:11ff:switchpro", out var pad) && pad.IsSteamVirtual && pad.SteamType == "switchpro");
    }


    // ── the scroll motion ──

    [Fact]
    public void WheelNotchGoesTwoThirdsFurtherThanWpf()
    {
        // WPF: 3 lines of 16 units a notch = 48; 64 since 1.3, now 80 (players asked for faster, twice)
        Assert.Equal(80, ScrollMotion.WheelDistance(-120, 3, 800), 6);
        Assert.Equal(-80, ScrollMotion.WheelDistance(120, 3, 800), 6);
        // Settings' category list: 1000 units to scroll. A full flick of the wheel is ~5 notches:
        // WPF's 48 × 5 = 240 a flick, 4.2 flicks; now 400 a flick, 2.5 flicks
        Assert.True(1000 / (5 * 48.0) > 4);
        Assert.True(1000 / (5 * ScrollMotion.WheelDistance(-120, 3, 800)) <= 2.5);
    }

    [Fact]
    public void PrecisionTouchpadStepsAreNotRoundedToNotches()
    {
        // a touchpad sends many small deltas: ten of 12 are one notch, not ten
        var total = Enumerable.Range(0, 10).Sum(_ => ScrollMotion.WheelDistance(-12, 3, 800));
        Assert.Equal(ScrollMotion.WheelDistance(-120, 3, 800), total, 6);
    }

    [Fact]
    public void WheelPageMode()
    {
        // "one screen at a time" (lines = -1): most of the viewport
        Assert.Equal(680, ScrollMotion.WheelDistance(-120, -1, 800), 6);
    }

    [Fact]
    public void GlideEasesOutAndArrives()
    {
        var m = new ScrollMotion();
        m.Jump(0);
        m.GlideBy(100, 1000);
        var positions = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            positions.Add(m.Step(1.0 / 60, 1000));
        }
        // moving at once (no dead first frame), never back, never past the target
        Assert.True(positions[0] > 3);
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] >= positions[i - 1]);
            Assert.True(positions[i] <= 100);
        }
        // it picks up speed over the first frames (no jolt: the old glide did a fifth of the way in one frame)...
        Assert.True(positions[0] < 12);
        Assert.True(positions[1] - positions[0] > positions[0]);
        // ...is most of the way within a tenth of a second, and then eases out: each step smaller
        Assert.True(positions[5] > 55);
        for (var i = 6; i < positions.Count; i++)
        {
            Assert.True(positions[i] - positions[i - 1] <= positions[i - 1] - positions[i - 2] + 0.3); // the last step snaps the last quarter unit
        }
        Assert.Equal(100, positions[^1]);
        Assert.False(m.Moving);
        Assert.Equal(0, m.Velocity);
    }

    [Fact]
    public void ANewTargetKeepsTheSpeed()
    {
        // a held direction moves the target every 50 to 100 ms: the list must not stop and start at each one
        var m = new ScrollMotion();
        m.Jump(0);
        var speeds = new List<double>();
        for (var row = 1; row <= 12; row++)
        {
            m.GlideTo(row * 104, 100000); // the next row comes into view
            for (var frame = 0; frame < 4; frame++) // 67 ms at 60 frames a second
            {
                var before = m.Velocity;
                m.Step(1.0 / 60, 100000);
                if (row > 4)
                {
                    speeds.Add(m.Velocity);
                    // from one frame to the next the speed changes by a fraction, never by a jump
                    Assert.True(Math.Abs(m.Velocity - before) < 0.35 * Math.Max(m.Velocity, before));
                }
            }
        }
        // once under way it is one even movement: about a row every 67 ms, within a fifth
        Assert.InRange(speeds.Min(), 104 / 0.0667 * 0.8, 104 / 0.0667 * 1.2);
        Assert.InRange(speeds.Max(), 104 / 0.0667 * 0.8, 104 / 0.0667 * 1.2);
    }

    [Fact]
    public void GlideIsTheSameAtAnyFrameRate()
    {
        double At(int fps, double seconds)
        {
            var m = new ScrollMotion();
            m.Jump(0);
            m.GlideBy(500, 1000);
            var p = 0.0;
            for (var i = 0; i < (int)Math.Round(seconds * fps); i++)
            {
                p = m.Step(1.0 / fps, 1000);
            }
            return p;
        }
        Assert.Equal(At(60, 1.0 / 6), At(144, 1.0 / 6), 0); // 10, 24 and 5 frames
        Assert.Equal(At(60, 1.0 / 6), At(30, 1.0 / 6), 0);
        // ~ 95% of the way after 4.75 time constants (a critically damped spring), about 210 ms
        Assert.InRange(At(60, 4.75 * ScrollMotion.GlideTime), 470, 480);
        Assert.InRange(4.75 * ScrollMotion.GlideTime, 0.15, 0.25);
    }

    [Fact]
    public void FastWheelSpinsChain()
    {
        var m = new ScrollMotion();
        m.Jump(0);
        var notch = ScrollMotion.WheelDistance(-120, 3, 800);
        // five notches 20 ms apart: the target is five notches away, wherever the glide was
        for (var i = 0; i < 5; i++)
        {
            m.GlideBy(notch, 10000);
            m.Step(0.02, 10000);
        }
        Assert.Equal(5 * notch, m.Target, 6);
        Assert.True(m.Position < m.Target);
        for (var i = 0; i < 60; i++)
        {
            m.Step(1.0 / 60, 10000);
        }
        Assert.Equal(5 * notch, m.Position, 6);
    }

    [Fact]
    public void GlideStopsAtTheEnds()
    {
        var m = new ScrollMotion();
        m.Jump(950);
        m.GlideBy(500, 1000);
        Assert.Equal(1000, m.Target);
        m.GlideBy(-5000, 1000);
        Assert.Equal(0, m.Target);
    }

    [Fact]
    public void ContentThatShrinksMidGlideOnlyClamps()
    {
        var m = new ScrollMotion();
        m.Jump(0);
        m.GlideTo(900, 1000);
        m.Step(1.0 / 60, 1000);
        var p = m.Step(1.0 / 60, 400); // the list got shorter
        Assert.InRange(p, 0, 400);
        for (var i = 0; i < 60; i++)
        {
            p = m.Step(1.0 / 60, 400);
        }
        Assert.Equal(400, p);
        Assert.False(m.Moving);
    }

    [Fact]
    public void FlickCoastsAndSlowsDown()
    {
        var m = new ScrollMotion();
        m.Jump(1000);
        m.Fling(2000);
        var last = 1000.0;
        var lastStep = double.MaxValue;
        while (m.Moving)
        {
            var p = m.Step(1.0 / 60, 100000);
            Assert.True(p > last);
            Assert.True(p - last < lastStep + 1e-9);
            lastStep = p - last;
            last = p;
        }
        // comes to rest near velocity × friction time
        Assert.InRange(last - 1000, ScrollMotion.CoastDistance(2000) * 0.95, ScrollMotion.CoastDistance(2000) * 1.01);
    }

    [Fact]
    public void FlickIsTheSameAtAnyFrameRate()
    {
        double Rest(int fps)
        {
            var m = new ScrollMotion();
            m.Jump(0);
            m.Fling(1500);
            var p = 0.0;
            while (m.Moving)
            {
                p = m.Step(1.0 / fps, 100000);
            }
            return p;
        }
        Assert.Equal(Rest(60), Rest(144), 0);
        Assert.Equal(Rest(60), Rest(30), 0);
    }

    [Fact]
    public void FlickStopsAtTheEnd()
    {
        var m = new ScrollMotion();
        m.Jump(900);
        m.Fling(5000);
        while (m.Moving)
        {
            m.Step(1.0 / 60, 1000);
        }
        Assert.Equal(1000, m.Position);
    }

    [Fact]
    public void SlowReleaseDoesNotCoast()
    {
        var m = new ScrollMotion();
        m.Jump(10);
        m.Fling(5);
        Assert.False(m.Moving);
    }

    [Fact]
    public void StickDrivesAtItsSpeedEveryFrame()
    {
        // the stick is read by a timer (60 times a second at best, unevenly); the list moves every frame at the speed it asked for
        double After(int fps, double seconds)
        {
            var m = new ScrollMotion();
            m.Jump(0);
            var poll = 0.0;
            for (var i = 0; i < (int)Math.Round(seconds * fps); i++)
            {
                poll += 1.0 / fps;
                if (poll >= 1.0 / 60 || i == 0)
                {
                    poll = 0;
                    m.Drive(1000, 100000);
                }
                m.Step(1.0 / fps, 100000);
            }
            Assert.InRange(m.Velocity, 990, 1000);
            return m.Position;
        }
        // the same distance at any frame rate; a little less than speed × time (it eased in)
        Assert.Equal(After(60, 1), After(144, 1), 0);
        Assert.Equal(After(60, 1), After(30, 1), 0);
        Assert.InRange(After(60, 1), 1000 - 1000 * ScrollMotion.DriveTime - 1, 1000);
    }

    [Fact]
    public void StickLetGoSettlesAndSilenceLetsGoToo()
    {
        var m = new ScrollMotion();
        m.Jump(0);
        for (var i = 0; i < 60; i++)
        {
            m.Drive(1000, 100000);
            m.Step(1.0 / 60, 100000);
        }
        var at = m.Position;
        m.Drive(0, 100000);
        while (m.Moving)
        {
            var before = m.Position;
            m.Step(1.0 / 60, 100000);
            Assert.True(m.Position >= before); // on a little, never back
        }
        Assert.InRange(m.Position - at, 50, 150);

        // the last word of the stick lost (the window lost the controller): it stops by itself
        m.Jump(0);
        m.Drive(1000, 100000);
        for (var i = 0; i < 120 && m.Moving; i++)
        {
            m.Step(1.0 / 60, 100000);
        }
        Assert.False(m.Moving);
        Assert.InRange(m.Position, 50, 400);

        // an end is a wall
        m.Jump(990);
        for (var i = 0; i < 30; i++)
        {
            m.Drive(2000, 1000);
            m.Step(1.0 / 60, 1000);
        }
        Assert.Equal(1000, m.Position);
    }

    // ── tap or drag ──

    [Fact]
    public void APressThatStaysWithinTheSlopIsATap()
    {
        var t = new TapTracker();
        t.Press(100, 100);
        Assert.False(t.Move(104, 103));
        Assert.False(t.Move(100 + TapTracker.Slop, 100)); // on the edge: still a tap
        Assert.False(t.Dragged);
        Assert.True(t.Release());
    }

    [Fact]
    public void APressThatLeavesTheSlopIsADragForGood()
    {
        var t = new TapTracker();
        t.Press(100, 100);
        Assert.False(t.Move(100, 108));
        Assert.True(t.Move(100, 111));   // the move that makes it a drag, said once
        Assert.False(t.Move(100, 160));
        Assert.False(t.Move(100, 100));  // back where it began: a drag all the same
        Assert.True(t.Dragged);
        Assert.False(t.Release());
        Assert.True(t.Dragged);          // readable by the handlers of that release
        // diagonal: the distance counts, not each axis
        t.Press(0, 0);
        Assert.False(t.Dragged);
        Assert.True(t.Move(8, 8));
        // moves without a press are nothing
        t.Release();
        t.Press(0, 0);
        t.Release();
        Assert.False(t.Move(50, 50));
        Assert.False(t.Dragged);
        Assert.InRange(TapTracker.Slop, 8, 10);
    }

    [Fact]
    public void StickScrollsFasterWhenHeld()
    {
        Assert.Equal(0, ScrollMotion.StickVelocity(0.15, 0));
        // down (negative Y in XInput) scrolls down (positive offset)
        Assert.True(ScrollMotion.StickVelocity(-1, 0) > 0);
        Assert.True(ScrollMotion.StickVelocity(1, 0) < 0);
        var start = ScrollMotion.StickVelocity(-1, 0);
        var held = ScrollMotion.StickVelocity(-1, ScrollMotion.StickRampTime);
        Assert.Equal(ScrollMotion.StickBoost, held / start, 6);
        // a full tilt goes at least as fast as a fast wheel (a notch every 50 ms) at once
        Assert.True(start >= ScrollMotion.WheelDistance(-120, 3, 800) * 20);
        // fine control near the middle
        Assert.True(ScrollMotion.StickVelocity(-0.4, 0) < start * 0.1);
    }

    [Fact]
    public void HeldDirectionRepeatsFaster()
    {
        Assert.Equal(ScrollMotion.SlowRepeat, ScrollMotion.RepeatInterval(TimeSpan.Zero));
        Assert.Equal(ScrollMotion.FastRepeat, ScrollMotion.RepeatInterval(TimeSpan.FromSeconds(3)));
        Assert.True(ScrollMotion.RepeatInterval(TimeSpan.FromSeconds(0.75)) < ScrollMotion.SlowRepeat);
    }

    // ── contrast ──

    private static Dictionary<string, Rgba> PaletteFile(string theme)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes", $"Colors.{theme}.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => Rgba.Parse((string)e.Attribute("Color")!));
    }

    private static Rgba After(Dictionary<string, Rgba> palette, Dictionary<string, Rgba> derived, string key) =>
        derived.TryGetValue(key, out var c) ? c : palette[key];

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void ContrastStepsSpreadTheSurfaces(string theme)
    {
        var palette = PaletteFile(theme);
        double Gap(string step)
        {
            var d = Palette.Derive(palette, Palette.Factor(step));
            return Palette.ContrastRatio(After(palette, d, "Brush.SurfaceAlt"), After(palette, d, Palette.Background));
        }
        var gaps = Palette.Steps.Select(Gap).ToList();
        for (var i = 1; i < gaps.Count; i++)
        {
            Assert.True(gaps[i] > gaps[i - 1], $"{theme}: {Palette.Steps[i]} {gaps[i]:0.00} after {gaps[i - 1]:0.00}");
        }
        // the normal step is the file itself
        var normal = Palette.Derive(palette, 1.0);
        foreach (var key in Palette.Surfaces.Concat(Palette.Lines).Append(Palette.Background))
        {
            Assert.Equal(palette[key], After(palette, normal, key));
        }
    }

    [Fact]
    public void DarkDockStandsOutFromThePage()
    {
        // the player's screenshot: the dock (SurfaceAlt) was barely there on the dark background (1.13:1)
        var dark = PaletteFile("Dark");
        Assert.True(Palette.ContrastRatio(dark["Brush.SurfaceAlt"], dark[Palette.Background]) >= 1.25);
        Assert.True(Palette.ContrastRatio(dark["Brush.Surface"], dark[Palette.Background]) >= 1.4);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void TextStaysReadableAtEveryStep(string theme)
    {
        var palette = PaletteFile(theme);
        foreach (var step in Palette.Steps)
        {
            var d = Palette.Derive(palette, Palette.Factor(step));
            var grounds = Palette.Surfaces.Append(Palette.Background).Select(k => After(palette, d, k)).ToList();
            foreach (var text in Palette.Texts)
            {
                foreach (var ground in grounds)
                {
                    Assert.True(Palette.ContrastRatio(After(palette, d, text), ground) >= Palette.MinTextContrast,
                        $"{theme} {step}: {text} {After(palette, d, text)} on {ground}");
                }
            }
            // the accent (links, the selected category, a fill too): AA for large text on the page and its cards
            foreach (var key in new[] { Palette.Background, "Brush.Surface", "Brush.SurfaceAlt", "Brush.Modal" })
            {
                Assert.True(Palette.ContrastRatio(palette["Brush.Accent"], After(palette, d, key)) >= 3,
                    $"{theme} {step}: accent on {key} {After(palette, d, key)}");
            }
            // the letter on a hint's disc is the page's own colour
            Assert.Equal(After(palette, d, Palette.Background), After(palette, d, "Brush.OnGlyph"));
        }
    }

    [Theory]
    [InlineData("higher", "higher")]
    [InlineData("HIGH", "high")]
    [InlineData("loud", "normal")]
    [InlineData(null, "normal")]
    public void ContrastSettingIsNormalized(string? value, string expected) => Assert.Equal(expected, Palette.Normalize(value));
}
