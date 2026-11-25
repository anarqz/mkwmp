// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

public class GameSettingsTests
{
    private static readonly Func<string, string?> NoEnv = _ => null;
    private static readonly string[] PeakTaken = ["auto", "100", "1000", "9999", "10000"];
    private static readonly string[] PeakRefused = ["99", "10001", "20000", "0", "1e3", "Auto", "100 nits"];
    private static readonly string[] WhiteTaken = ["auto", "40", "203", "999", "1000"];
    private static readonly string[] WhiteRefused = ["39", "1001", "5", "-40"];
    private static readonly string[] NeverIn016 = ["lan", "lan_ip", "lan_peers", "hdr_system", "wb_par", "mem_spill", "logic_hz_tt", "screen_raw"];

    /// <summary>The real mkw.ini of a 0.1.15 install, with the game's CRLF line ends (the repository keeps it with LF).</summary>
    private static string RealIni => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mkw.ini")).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static (MkwRecompTarget Target, GameInstall Install, string Ini, Action<bool> Run) Game(string? ini = null, bool mods = true)
    {
        var d = Rig.NewFolder("gamesettings");
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), mods ? Rig.TestModExe : Rig.TestExe);
        var path = Path.Combine(d, "mkw.ini");
        if (ini is not null)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(ini));
        }
        var running = false;
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => running };
        return (target, target.Detect(d)!, path, on => running = on);
    }

    private static Dictionary<string, string?> One(string id, string? value) => new() { [id] = value };

    private static string Apply(string text, params (string Key, string? Value)[] changes) =>
        RecompIni.Apply(text, [.. changes.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))], key => key switch
        {
            "VOLUME" or "AUDIO_CHANNELS" => "audio",
            "FULLSCREEN" or "FPS" or "WINDOW_SIZE" => "display",
            "ANISO" => "graphics",
            _ => null,
        });

    // ---- the schema -----------------------------------------------------------------------

    [Fact]
    public void Schema_is_well_formed()
    {
        var (target, install, _, _) = Game();
        IGameTarget t = target;
        var schema = t.GameSettings(install)!;
        Assert.Equal("screen graphics sound controls general advanced", string.Join(' ', schema.Categories.Select(c => c.Id)));
        Assert.True(schema.Categories[^1].Advanced);
        Assert.NotNull(schema.Categories[^1].Note);
        Assert.Equal(RecompSettings.NextStart, schema.Note);
        var all = schema.Settings.ToList();
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var s in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label), s.Id);
            Assert.False(string.IsNullOrWhiteSpace(s.Description), s.Id);
            Assert.Null(s.Locked);
            // the default is a value the setting takes ("" is a choice's automatic option)
            Assert.Equal(s.Default, s.Normalize(s.Default));
            if (s.Kind == GameSettingKind.Choice)
            {
                Assert.True(s.Options.Count >= 2, s.Id);
                Assert.Equal(s.Options.Count, s.Options.Select(o => o.Value).Distinct().Count());
            }
            else
            {
                Assert.Empty(s.Options);
            }
            if (s.Kind == GameSettingKind.Range)
            {
                Assert.True(s.Min < s.Max && s.Step > 0, s.Id);
            }
            if (s.VisibleWhen is { } when)
            {
                var other = schema.Find(when.Setting)!;
                Assert.All(when.Values, v => other.Normalize(v));
            }
            foreach (var (id, value) in s.Options.SelectMany(o => o.Sets ?? new Dictionary<string, string>()))
            {
                Assert.Equal(value, schema.Find(id)!.Normalize(value));
            }
        }
        // nothing the game or MKWMP's install depends on, and no hacks
        foreach (var never in new[] { "romfs", "exefs", "game_dir", "mods", "mods_played", "mods_ack", "player", "profile_dir", "turbo", "unlock_all", "gpu_index", "bind_a", "lan" })
        {
            Assert.Null(schema.Find(never));
        }
        Assert.Same(schema, t.GameSettings(install)); // the same object while nothing changed
    }

    [Fact]
    public void The_schema_follows_the_runtime_version()
    {
        var (_, install, ini, _) = Game();
        GameSettingsSchema For(string version) => RecompSettings.Schema(install with { Version = version }, NoEnv)!;
        Assert.Equal(["0.1.15", "0.1.15-mkwmp", "0.1.16", "0.1.16-mkwmp", "0.1.16-mkwmp.1", "0.1.17", "0.1.18"], RecompSettings.Runtimes);
        string[] rows016 = ["hdr_tonemap", "hdr_peak", "hdr_white", "keyboard_mouse", "gb_mature"];

        // upstream 0.1.15: no Logic rate (it came right after that release) and none of 0.1.16's rows
        var v15 = For("0.1.15");
        Assert.Null(v15.Find("logic_hz"));
        Assert.All(rows016, id => Assert.Null(v15.Find(id)));
        // the fork's 0.1.15-mkwmp is built from the later source: Logic rate, and still none of 0.1.16's
        var fork15 = For("0.1.15-mkwmp");
        Assert.Equal(("60", true), (fork15.Find("logic_hz")!.Default, fork15.Find("logic_hz")!.RequiresRestart));
        Assert.All(rows016, id => Assert.Null(fork15.Find(id)));
        Assert.Equal(v15.Settings.Count() + 1, fork15.Settings.Count());

        foreach (var version in new[] { "0.1.16", "0.1.16-mkwmp", "0.1.16-mkwmp.1", "0.1.17", "0.1.18" })
        {
            var s = For(version);
            Assert.NotNull(s.Find("logic_hz"));
            var has017 = version is "0.1.17" or "0.1.18";
            Assert.Equal(fork15.Settings.Count() + rows016.Length + (has017 ? Rows017.Length : 0) + (version == "0.1.18" ? Rows018.Length : 0), s.Settings.Count());
            Assert.All(Rows017, id => Assert.Equal(has017, s.Find(id) is not null));
            Assert.All(Rows018, id => Assert.Equal(version == "0.1.18", s.Find(id) is not null));
            // cfg.c metas: defaults, and what is read only at start
            var tonemap = s.Find("hdr_tonemap")!;
            Assert.Equal((GameSettingKind.Choice, "vanilla", true, "HDR tone mapper"), (tonemap.Kind, tonemap.Default, tonemap.RequiresRestart, tonemap.Label));
            Assert.Equal(["vanilla", "psychov"], tonemap.Options.Select(o => o.Value));
            Assert.Equal(["Vanilla", "PsychoV35"], tonemap.Options.Select(o => o.Label));
            Assert.Equal(("hdr", "auto"), (tonemap.VisibleWhen!.Setting, tonemap.VisibleWhen.Values.Single()));
            var peak = s.Find("hdr_peak")!;
            var white = s.Find("hdr_white")!;
            Assert.Equal((GameSettingKind.Text, "auto", false), (peak.Kind, peak.Default, peak.RequiresRestart));
            Assert.Equal((GameSettingKind.Text, "auto", false), (white.Kind, white.Default, white.RequiresRestart));
            Assert.All(new[] { peak, white }, x => Assert.Equal(("hdr_tonemap", "psychov"), (x.VisibleWhen!.Setting, x.VisibleWhen.Values.Single())));
            Assert.All(PeakTaken, v => Assert.Equal(v, peak.Normalize(v)));
            Assert.All(PeakRefused, v => Assert.Throws<LauncherException>(() => peak.Normalize(v)));
            Assert.All(WhiteTaken, v => Assert.Equal(v, white.Normalize(v)));
            Assert.All(WhiteRefused, v => Assert.Throws<LauncherException>(() => white.Normalize(v)));
            var keys = s.Find("keyboard_mouse")!;
            Assert.Equal((GameSettingKind.Toggle, "1", false, "Keyboard and mouse"), (keys.Kind, keys.Default, keys.RequiresRestart, keys.Label));
            var nsfw = s.Find("gb_mature")!;
            Assert.Equal(("obfuscate", false, "NSFW mods"), (nsfw.Default, nsfw.RequiresRestart, nsfw.Label));
            Assert.Equal(["obfuscate", "show"], nsfw.Options.Select(o => o.Value));
            // where the game's menu has them: after HDR on Screen, before the mouse sensitivity on Controls, on General
            var screen = s.Categories.Single(c => c.Id == "screen").Settings.Select(x => x.Id).ToList();
            Assert.Equal(["hdr", "hdr_tonemap", "hdr_peak", "hdr_white"], screen[^4..]);
            var controls = s.Categories.Single(c => c.Id == "controls").Settings.Select(x => x.Id).ToList();
            Assert.Equal(["stick_deadzone", "keyboard_mouse", "mouse_sens"], controls[^3..]);
            Assert.Equal("gb_mature", s.Categories.Single(c => c.Id == "general").Settings[^1].Id);
            // LAN play stays out, with 0.1.16's LAN network row; so do the keys the game keeps for itself
            Assert.All(NeverIn016, id => Assert.Null(s.Find(id)));
            Assert.Equal(s.Settings.Count(), s.Settings.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(s.Settings, x => Assert.Equal(x.Default, x.Normalize(x.Default)));
            // a game set up before 0.1.16 has no tone mapper programs yet: the row is locked, as the game greys it out
            Assert.Equal(RecompSettings.TonemapMissing, tonemap.Locked);
            // the launcher keeps none of mkw-recomp's settings: the update check and the start screen are the player's
            string[] owned = [];
            Assert.All(owned, id => Assert.NotNull(s.Find(id)!.Locked));
            Assert.All(s.Settings.Where(x => x.Id != "hdr_tonemap" && !owned.Contains(x.Id)), x => Assert.Null(x.Locked));
        }

        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        var v16 = install with { Version = "0.1.16" };
        Assert.Contains("made by the setup", Assert.Throws<LauncherException>(() => RecompSettings.Write(target, v16, One("hdr_tonemap", "psychov"), NoEnv)).Message, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(ini)!, RecompSettings.TonemapFolder));
        Assert.Null(RecompSettings.Schema(v16, NoEnv)!.Find("hdr_tonemap")!.Locked);
        // written under the sections cfg.c gives them
        RecompSettings.Write(target, v16, new Dictionary<string, string?> { ["hdr_tonemap"] = "psychov", ["hdr_peak"] = "1200", ["keyboard_mouse"] = "0", ["gb_mature"] = "show" }, NoEnv);
        var text = File.ReadAllText(ini).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("[display]\nhdr_tonemap = psychov\nhdr_peak = 1200\n", text, StringComparison.Ordinal);
        Assert.Contains("[input]\nkeyboard_mouse = 0\n", text, StringComparison.Ordinal);
        Assert.Contains("[launcher]\ngb_mature = show\n", text, StringComparison.Ordinal);
        var read = RecompSettings.Read(v16, NoEnv);
        Assert.Equal(("psychov", "1200", "0", "show"), (read["hdr_tonemap"], read["hdr_peak"], read["keyboard_mouse"], read["gb_mature"]));
        // the same file under upstream 0.1.15: those keys are not its settings
        Assert.False(RecompSettings.Read(install with { Version = "0.1.15" }, NoEnv).ContainsKey("keyboard_mouse"));
        Assert.Throws<LauncherException>(() => RecompSettings.Write(target, install with { Version = "0.1.15" }, One("logic_hz", "120"), NoEnv));
        RecompSettings.Write(target, install with { Version = "0.1.15-mkwmp" }, One("logic_hz", "120"), NoEnv);
    }

    /// <summary>0.1.17's new rows (settings_tabs.cpp tab_graphics, #462) and the two keys without a menu row (#644, #742).</summary>
    private static readonly string[] Rows017 = ["temporal", "temporal_quality", "dlss_preset", "cas", "cas_sharpness", "fg_engine", "reflex", "shadow_draws", "pad_p2"];

    /// <summary>0.1.18's Controls row (RenHDrum, #803) and the two keys without a menu row its release asks players to try (#481).</summary>
    private static readonly string[] Rows018 = ["renhdrum", "tex_pressure", "vram_log"];

    /// <summary>0.1.18's keys that stay out: Linux's amdgpu clocks and udmabuf pools, and a renderer experiment.</summary>
    private static readonly string[] NeverIn018 = ["gpu_clocks", "udmabuf", "pass_hazards"];

    [Fact]
    public void The_018_rows_are_the_game_s_with_their_places_sections_and_yes_no_words()
    {
        var (_, install, ini, _) = Game();
        var v18 = install with { Version = "0.1.18" };
        var s = RecompSettings.Schema(v18, NoEnv)!;
        var renhdrum = s.Find("renhdrum")!;
        Assert.Equal((GameSettingKind.Toggle, "0", true, "HD vibration (RenHDrum)"), (renhdrum.Kind, renhdrum.Default, renhdrum.RequiresRestart, renhdrum.Label));
        var pressure = s.Find("tex_pressure")!;
        Assert.Equal((GameSettingKind.Toggle, "0", true, "Textures and memory"), (pressure.Kind, pressure.Default, pressure.RequiresRestart, pressure.Group));
        var vram = s.Find("vram_log")!;
        Assert.Equal(("", true, "Diagnostics"), (vram.Default, vram.RequiresRestart, vram.Group));
        Assert.Equal(["", "1800"], vram.Options.Select(o => o.Value));
        // where the game's menu has RenHDrum: under Trigger vibration; the two keys with the rows of their kind
        var controls = s.Categories.Single(c => c.Id == "controls").Settings.Select(x => x.Id).ToList();
        Assert.Equal("renhdrum", controls[controls.IndexOf("rumble_triggers") + 1]);
        var advanced = s.Categories.Single(c => c.Id == "advanced").Settings.ToList();
        Assert.Equal("tex_pressure", advanced[advanced.FindLastIndex(x => x.Group == "Textures and memory")].Id);
        Assert.Equal("vram_log", advanced[^1].Id);
        // still never: Linux-only keys and the renderer experiment
        Assert.All(NeverIn018, id => Assert.Null(s.Find(id)));
        // none of them on 0.1.17
        Assert.All(Rows018, id => Assert.Null(RecompSettings.Schema(install with { Version = "0.1.17" }, NoEnv)!.Find(id)));

        // sections: [input] as cfg.c's row; the two K keys beside their kind
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        RecompSettings.Write(target, v18, new Dictionary<string, string?> { ["renhdrum"] = "1", ["tex_pressure"] = "1", ["vram_log"] = "1800" }, NoEnv);
        var text = File.ReadAllText(ini).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("[input]\nrenhdrum = 1\n", text, StringComparison.Ordinal);
        Assert.Contains("[performance]\ntex_pressure = 1\n", text, StringComparison.Ordinal);
        Assert.Contains("[diagnostics]\nvram_log = 1800\n", text, StringComparison.Ordinal);
        var read = RecompSettings.Read(v18, NoEnv);
        Assert.Equal(("1", "1", "1800"), (read["renhdrum"], read["tex_pressure"], read["vram_log"]));

        // 0.1.18's cfg_bool (#756): off / false / no and on / true / yes, any case; a word it does not know is the default
        File.WriteAllText(ini, "[launcher]\ndiscord = Off\nupdate_check = no\n[input]\nmotion = FALSE\nswitch2 = yes\nrumble_triggers = 2\ndsu = maybe\n");
        read = RecompSettings.Read(v18, NoEnv);
        Assert.Equal(("0", "0", "0", "1", "1", "0"), (read["discord"], read["update_check"], read["motion"], read["switch2"], read["rumble_triggers"], read["dsu"]));
        // before 0.1.18 only "0" was false: the value is shown as the file has it
        Assert.Equal("Off", RecompSettings.Read(install with { Version = "0.1.17" }, NoEnv)["discord"]);
        Assert.Equal("1", RecompSettings.YesNo(" ON ", "0"));
        Assert.Equal("0", RecompSettings.YesNo("-0", "1"));
    }

    [Fact]
    public void The_libraries_are_the_ones_the_game_s_own_menu_would_download()
    {
        Func<string, string?> With(params (string Id, string Value)[] values) => id => values.FirstOrDefault(v => v.Id == id).Value;
        Assert.Empty(RecompLibs.Wanted(With(("temporal", "off"), ("framegen", "off"), ("fg_engine", "flow"), ("renhdrum", "0")), nvidia: true));
        Assert.Equal(["dlss"], RecompLibs.Wanted(With(("temporal", "dlss"), ("framegen", "off"), ("fg_engine", "dlss")), nvidia: true));
        // DLSS's files only for an NVIDIA card: its licence's condition, as libs_sync
        Assert.Empty(RecompLibs.Wanted(With(("temporal", "dlss"), ("framegen", "2"), ("fg_engine", "dlss")), nvidia: false));
        Assert.Equal(["dlss", "dlssg"], RecompLibs.Wanted(With(("temporal", "dlss"), ("framegen", "auto"), ("fg_engine", "dlss")), nvidia: true));
        Assert.Equal(["fsr3", "fsr3fg", "renhdrum"], RecompLibs.Wanted(With(("temporal", "fsr3"), ("framegen", "2"), ("fg_engine", "fsr3"), ("renhdrum", "1")), nvidia: false));
        // the engine without frame generation needs nothing
        Assert.Equal(["renhdrum"], RecompLibs.Wanted(With(("framegen", "off"), ("fg_engine", "fsr3"), ("renhdrum", "1")), nvidia: true));

        // libs.tsv as 0.1.18 ships it: its Windows rows only, and never a path for a file name
        const string Tsv = "feature\tos\tfile\tbytes\tsha256\turl\n"
            + "dlss\twindows\tnvngx_dlss.dll\t58956912\td00b\thttps://x/nvngx_dlss.dll\n"
            + "dlss\tlinux\tlibnvidia-ngx-dlss.so.310.9.16\t59481944\t3098\thttps://x/l\n"
            + "renhdrum\twindows\trenhdrum.dll\t823808\tc685\thttps://x/renhdrum.dll\n"
            + "fsr3\twindows\t..\\evil.dll\t1\t00\thttps://x/e\n";
        Assert.Equal([("dlss", "nvngx_dlss.dll", 58956912L), ("renhdrum", "renhdrum.dll", 823808L)], RecompLibs.Table(Tsv));
        Assert.Empty(RecompLibs.Table("not a table"));

        // what is missing: the settings' pick, against the files beside mkw.exe; nothing for a runtime without libs.py
        var (_, install, ini, _) = Game();
        var v18 = install with { Version = "0.1.18" };
        File.WriteAllText(ini, "[input]\nrenhdrum = 1\n[display]\ntemporal = dlss\n");
        Assert.Empty(RecompLibs.Missing(v18, nvidia: true));
        var prepare = Path.Combine(install.Root, "setup", "src", "tools", "prepare");
        Directory.CreateDirectory(prepare);
        File.WriteAllText(Path.Combine(prepare, "libs.tsv"), Tsv);
        File.WriteAllText(Path.Combine(prepare, "libs.py"), "");
        Assert.Equal(["nvngx_dlss.dll", "renhdrum.dll"], RecompLibs.Missing(v18, nvidia: true).Select(l => l.File));
        Assert.Equal(["renhdrum.dll"], RecompLibs.Missing(v18, nvidia: false).Select(l => l.File));
        Assert.Equal("NVIDIA DLSS, RenHDrum", RecompLibs.NamesOf(RecompLibs.Missing(v18, nvidia: true)));
        File.WriteAllBytes(Path.Combine(install.Root, "renhdrum.dll"), [1]); // a file already there is kept, whatever it is
        Assert.Equal(["nvngx_dlss.dll"], RecompLibs.Missing(v18, nvidia: true).Select(l => l.File));

        // libs.py's events
        Assert.Equal((5L, 10L), RecompLibs.Progress("progress 5 10"));
        Assert.Null(RecompLibs.Progress("ready renhdrum.dll"));
        Assert.Equal("nvngx_dlss.dll came damaged", RecompLibs.Error("error fetch nvngx_dlss.dll came damaged"));
        Assert.Null(RecompLibs.Error("done"));
        // the install's own Python and libs.py, with the features once each and this folder
        var start = RecompLibs.Command(v18, ["fsr3", "fsr3", "fsr3fg"]);
        Assert.Equal(RecompSetup.Python(v18), start.FileName);
        Assert.Equal(["-I", Path.Combine(prepare, "libs.py"), "fetch", "--features", "fsr3,fsr3fg", "--dir", install.Root, "--os", "windows"], start.ArgumentList);
    }

    [Fact]
    public void The_017_rows_are_the_game_s_with_its_values_defaults_and_places()
    {
        var (_, install, ini, _) = Game();
        var v17 = install with { Version = "0.1.17" };
        var s = RecompSettings.Schema(v17, NoEnv)!;
        // cfg.c rows: default, values, whether the game reads it only at start
        void Is(string id, string def, bool restart, params string[] values)
        {
            var x = s.Find(id)!;
            Assert.Equal((def, restart), (x.Default, x.RequiresRestart));
            if (values.Length > 0)
            {
                Assert.Equal(values, x.Options.Select(o => o.Value));
            }
        }
        Is("temporal", "off", false, "off", "dlss", "fsr3");
        Is("temporal_quality", "quality", true, "native", "quality", "balanced", "performance", "ultra");
        Is("dlss_preset", "default", false, "default", "j", "k", "l", "m");
        Is("cas", "off", false, "off", "on");
        Is("cas_sharpness", "50", false);
        Is("fg_engine", "flow", false, "flow", "dlss", "fsr3");
        Is("reflex", "off", false, "off", "on", "boost");
        Is("shadow_draws", "1", true, "1", "auto", "0");
        Is("pad_p2", "0", false);
        // the game's own texts (mkw.pot) and product names, kept as the game writes them
        Assert.Equal(("AI upscaling", "NVIDIA DLSS", "AMD FSR 3.1"), (s.Find("temporal")!.Label, s.Find("temporal")!.Options[1].Label, s.Find("temporal")!.Options[2].Label));
        Assert.Equal(["MKW Recomp", "NVIDIA DLSS", "AMD FSR 3.1"], s.Find("fg_engine")!.Options.Select(o => o.Label));
        Assert.Equal("On + Boost", s.Find("reflex")!.Options[2].Label);
        Assert.Contains("NVIDIA GPUs only", s.Find("reflex")!.Description, StringComparison.Ordinal);
        Assert.Contains("RTX", s.Find("temporal")!.Description, StringComparison.Ordinal);
        // shown with what they act on
        Assert.Equal(["dlss", "fsr3"], s.Find("temporal_quality")!.VisibleWhen!.Values);
        Assert.Equal(("temporal", "dlss"), (s.Find("dlss_preset")!.VisibleWhen!.Setting, s.Find("dlss_preset")!.VisibleWhen!.Values.Single()));
        Assert.Equal(("cas", "on"), (s.Find("cas_sharpness")!.VisibleWhen!.Setting, s.Find("cas_sharpness")!.VisibleWhen!.Values.Single()));
        Assert.DoesNotContain("off", s.Find("fg_engine")!.VisibleWhen!.Values);
        // where the game's menu has them: under the FSR sharpness, and under Frame generation
        var graphics = s.Categories.Single(c => c.Id == "graphics").Settings.Select(x => x.Id).ToList();
        Assert.Equal(["sharpness", "temporal", "temporal_quality", "dlss_preset", "cas", "cas_sharpness", "operation_mode"], graphics.GetRange(graphics.IndexOf("sharpness"), 7));
        Assert.Equal(["framegen", "fg_engine", "reflex", "vrr"], graphics.GetRange(graphics.IndexOf("framegen"), 4));
        Assert.Equal("shadow_draws", graphics[graphics.IndexOf("shadow_size") + 1]);
        var controls = s.Categories.Single(c => c.Id == "controls").Settings.Select(x => x.Id).ToList();
        Assert.Equal("pad_p2", controls[controls.IndexOf("pad_layout") + 1]);
        // MKW_GPU_CLOCKS is Linux amdgpu only: never a row for the Windows build
        Assert.Null(s.Find("gpu_clocks"));
        // written under the sections cfg.c gives them ([graphics] for shadow_draws, which has none there)
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        RecompSettings.Write(target, v17, new Dictionary<string, string?> { ["temporal"] = "dlss", ["reflex"] = "boost", ["pad_p2"] = "1", ["shadow_draws"] = "auto" }, NoEnv);
        var text = File.ReadAllText(ini).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("temporal = dlss\n", text, StringComparison.Ordinal);
        Assert.Contains("reflex = boost\n", text, StringComparison.Ordinal);
        Assert.Contains("[input]\npad_p2 = 1\n", text, StringComparison.Ordinal);
        Assert.Contains("[graphics]\nshadow_draws = auto\n", text, StringComparison.Ordinal);
        Assert.Matches(@"\[display\]\n(?:[^\[]*\n)?temporal = dlss\n", text);
        var read = RecompSettings.Read(v17, NoEnv);
        Assert.Equal(("dlss", "boost", "1", "auto"), (read["temporal"], read["reflex"], read["pad_p2"], read["shadow_draws"]));
        Assert.Throws<LauncherException>(() => RecompSettings.Write(target, v17, One("temporal", "xess"), NoEnv));
        // the same keys are no settings of 0.1.16
        Assert.Throws<LauncherException>(() => RecompSettings.Write(target, install with { Version = "0.1.16" }, One("temporal", "dlss"), NoEnv));
        // an MKW_* in the environment locks the row, as the game greys it out
        Assert.NotNull(RecompSettings.Schema(v17, n => n == "MKW_REFLEX" ? "on" : null)!.Find("reflex")!.Locked);
    }

    [Fact]
    public void Only_runtimes_whose_settings_are_known_have_a_schema()
    {
        var (old, oldInstall, _, _) = Game(mods: false); // 0.1.10
        IGameTarget t = old;
        Assert.Null(t.GameSettings(oldInstall));
        Assert.Empty(t.ReadGameSettings(oldInstall));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(oldInstall, One("volume", "50")));

        var (_, install, _, _) = Game();
        Assert.Null(RecompSettings.Schema(install with { TargetId = "some-emulator" }));
        Assert.Null(RecompSettings.PathFor(install, name => name == "MKW_GAME_DIR" ? @"D:\other" : null));
    }

    [Fact]
    public void Defaults_follow_the_steam_deck_and_big_picture_and_the_environment_locks()
    {
        var (_, install, _, _) = Game();
        var desktop = RecompSettings.Schema(install, NoEnv)!;
        Assert.Equal("0", desktop.Find("fullscreen")!.Default);
        Assert.Equal("original", desktop.Find("resolution")!.Default);

        var deck = RecompSettings.Schema(install, name => name == "SteamDeck" ? "1" : null)!;
        Assert.Equal("1", deck.Find("fullscreen")!.Default);
        Assert.Equal("1280x800", deck.Find("window_size")!.Default);
        Assert.Contains(deck.Find("window_size")!.Options, o => o.Value == "1280x800");
        Assert.Equal("fifo", deck.Find("vk_present")!.Default);
        Assert.Equal("720", deck.Find("resolution")!.Default);
        // device_profile = desktop turns the Deck's defaults off
        var off = RecompSettings.Schema(install, name => name switch { "SteamDeck" => "1", "MKW_DEVICE_PROFILE" => "desktop", _ => null })!;
        Assert.Equal("0", off.Find("fullscreen")!.Default);

        var bigPicture = RecompSettings.Schema(install, name => name == "SteamGamepadUI" ? "1" : null)!;
        Assert.Equal("1", bigPicture.Find("fullscreen")!.Default);
        Assert.Equal("original", bigPicture.Find("resolution")!.Default);

        Func<string, string?> env = name => name == "MKW_FPS" ? "30" : null;
        var locked = RecompSettings.Schema(install, env)!;
        Assert.Contains("MKW_FPS", locked.Find("fps")!.Locked, StringComparison.Ordinal);
        Assert.Null(locked.Find("volume")!.Locked);
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        Assert.Contains("MKW_FPS", Assert.Throws<LauncherException>(() => RecompSettings.Write(target, install, One("fps", "60"), env)).Message, StringComparison.Ordinal);
    }

    // ---- values ---------------------------------------------------------------------------

    [Fact]
    public void Values_are_checked_and_written_in_the_games_spelling()
    {
        var toggle = GameSetting.Toggle("t", "T", "d", false, false);
        Assert.Equal("1", toggle.Normalize("1"));
        Assert.Throws<LauncherException>(() => toggle.Normalize("true"));

        var choice = GameSetting.Choice("c", "C", "d", "", true, new GameSettingOption("", "Automatic"), new GameSettingOption("a", "A"));
        Assert.Equal("a", choice.Normalize("a"));
        Assert.Equal("", choice.Normalize(""));
        Assert.Throws<LauncherException>(() => choice.Normalize("A"));
        Assert.Throws<LauncherException>(() => choice.Normalize("b"));

        var whole = GameSetting.Range("r", "R", "d", 130, false, 0, 300, 10, "%");
        Assert.Equal("130", whole.Default);
        Assert.Equal("300", whole.Normalize("300"));
        Assert.Equal("45", whole.Normalize("45.0"));
        Assert.Throws<LauncherException>(() => whole.Normalize("301"));
        Assert.Throws<LauncherException>(() => whole.Normalize("-1"));
        Assert.Throws<LauncherException>(() => whole.Normalize("NaN"));
        Assert.Throws<LauncherException>(() => whole.Normalize("1,5"));
        Assert.Throws<LauncherException>(() => whole.Normalize("loud"));

        var curve = GameSetting.Range("k", "K", "d", 0.6, false, 0.2, 1.5, 0.05);
        Assert.Equal(2, curve.Decimals);
        Assert.Equal("0.6", curve.Default);
        Assert.Equal("0.65", curve.Normalize("0.65000001"));
        Assert.Equal("1.5", curve.Normalize("1.50"));
        Assert.Throws<LauncherException>(() => curve.Normalize("0.1"));

        var text = GameSetting.Text("x", "X", "d", "127.0.0.1:26760", false, 20, @"[A-Za-z0-9.\-]+:[0-9]{1,5}");
        Assert.Equal("pc.local:26760", text.Normalize("pc.local:26760"));
        Assert.Equal("", text.Normalize(""));
        Assert.Throws<LauncherException>(() => text.Normalize("no port"));
        Assert.Throws<LauncherException>(() => text.Normalize("a:1\nmods = x"));
        Assert.Throws<LauncherException>(() => text.Normalize("a-very-long-host-name.example:26760"));
    }

    [Fact]
    public void Unknown_settings_and_bad_values_are_refused_and_write_nothing()
    {
        var (target, install, ini, _) = Game(RealIni);
        IGameTarget t = target;
        var before = File.ReadAllBytes(ini);
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("volume", "101")));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("vk_present", "vsync")));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("fullscreen", "yes")));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("dsu_server", "127.0.0.1")));
        // not settings: the paths and the mods list are the game's and MKWMP's
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("romfs", @"C:\x.nsp")));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("mods", "")));
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("game_dir", @"C:\x")));
        // one bad value refuses the whole change
        Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, new Dictionary<string, string?> { ["volume"] = "50", ["fps"] = "999" }));
        Assert.Equal(before, File.ReadAllBytes(ini));
    }

    [Fact]
    public void Refuses_while_the_game_runs_and_a_read_only_file()
    {
        var (target, install, ini, run) = Game(RealIni);
        IGameTarget t = target;
        var before = File.ReadAllBytes(ini);
        run(true);
        Assert.Equal(RecompSettings.GameIsRunning, Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("volume", "50"))).Message);
        Assert.Equal(RecompSettings.GameIsRunning, Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("fullscreen", null))).Message);
        Assert.Equal(before, File.ReadAllBytes(ini));
        run(false);
        File.SetAttributes(ini, FileAttributes.ReadOnly);
        try
        {
            Assert.Contains("read-only", Assert.Throws<LauncherException>(() => t.WriteGameSettings(install, One("volume", "50"))).Message, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(ini));
        }
        finally
        {
            File.SetAttributes(ini, FileAttributes.Normal);
        }
        t.WriteGameSettings(install, One("volume", "50"));
        Assert.Equal("50", t.ReadGameSettings(install)["volume"]);
    }

    // ---- mkw.ini --------------------------------------------------------------------------

    [Fact]
    public void The_real_ini_round_trips_byte_for_byte()
    {
        var real = RealIni;
        var (target, install, ini, _) = Game(real);
        IGameTarget t = target;
        Assert.Equal("1", Assert.Single(t.ReadGameSettings(install)).Value); // fullscreen; romfs and mods are not settings

        // a new key in a section the file lacks: the section goes at the end, with the file's line ends
        t.WriteGameSettings(install, One("volume", "50"));
        Assert.Equal(real + "\r\n[audio]\r\nvolume = 50\r\n", File.ReadAllText(ini));
        // a key the file has: only its value changes; a new key of a section the file has goes after that section's last line
        t.WriteGameSettings(install, new Dictionary<string, string?> { ["fullscreen"] = "0", ["fps"] = "30" });
        Assert.Equal(real.Replace("fullscreen = 1", "fullscreen = 0\r\nfps = 30", StringComparison.Ordinal) + "\r\n[audio]\r\nvolume = 50\r\n", File.ReadAllText(ini));
        Assert.Equal(new Dictionary<string, string> { ["fullscreen"] = "0", ["fps"] = "30", ["volume"] = "50" }, t.ReadGameSettings(install).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary());
        // writing the same again changes nothing; resetting takes the lines out (the added section's heading stays, as the game leaves it)
        var stamp = File.GetLastWriteTimeUtc(ini);
        t.WriteGameSettings(install, One("fps", "30"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ini));
        t.WriteGameSettings(install, new Dictionary<string, string?> { ["fullscreen"] = "1", ["fps"] = null, ["volume"] = "" });
        Assert.Equal(real + "\r\n[audio]\r\n", File.ReadAllText(ini));
        Assert.StartsWith(real, File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ini)!, "*.mkwmp-tmp"));
        // what MKWMP itself reads from the file is as it was
        Assert.Equal("mkwmp", Recomp.ModsIn(File.ReadAllText(ini)));
    }

    [Fact]
    public void Comments_unknown_keys_order_and_spelling_stay()
    {
        const string Text =
            "\u00EF\u00BB\u00BF# my settings\n"
            + "ROMFS = \"D:\\Jogos\\MKW.nsp\"\n"
            + "some_future_key = 7\n"
            + "\n"
            + "[display]\n"
            + "; windowed for streaming\n"
            + "  FullScreen=0\n"
            + "\tMKW_FPS   =   60\n"
            + "# window_size = 1280x720\n"
            + "volume = 10\n"
            + "\n"
            + "[graphics]\n"
            + "# aniso = 16\n"
            + "# tex_quality = half\n"
            + "\n"
            + "[audio]\n"
            + "volume = 80\n"
            + "no equals here\n"
            + "audio_channels =";
        var values = RecompIni.Values(Text);
        Assert.Equal("D:\\Jogos\\MKW.nsp", values["ROMFS"]);
        Assert.Equal("0", values["FULLSCREEN"]);
        Assert.Equal("60", values["FPS"]); // the MKW_ prefix is optional
        Assert.Equal("80", values["VOLUME"]); // the later line wins
        Assert.False(values.ContainsKey("AUDIO_CHANNELS")); // "key =" is unset
        Assert.False(values.ContainsKey("WINDOW_SIZE")); // a commented example

        Assert.Equal(Text, Apply(Text)); // nothing to change: nothing changes
        // the last line of the key gets the value; indentation and the key's spelling stay
        Assert.Equal(
            Text.Replace("  FullScreen=0", "  FullScreen = 1", StringComparison.Ordinal).Replace("\tMKW_FPS   =   60", "\tMKW_FPS = 30", StringComparison.Ordinal)
                .Replace("volume = 80", "volume = 35", StringComparison.Ordinal),
            Apply(Text, ("fullscreen", "1"), ("FPS", "30"), ("volume", "35")));
        // a missing key goes after its commented example, else after its section's last line; a file without a last newline gets one first
        Assert.Equal(
            Text.Replace("# window_size = 1280x720\n", "# window_size = 1280x720\nwindow_size = 1600x900\n", StringComparison.Ordinal)
                .Replace("# aniso = 16\n", "# aniso = 16\naniso = 8\n", StringComparison.Ordinal),
            Apply(Text, ("window_size", "1600x900"), ("aniso", "8")));
        Assert.Equal(Text.Replace("audio_channels =", "audio_channels = 6", StringComparison.Ordinal), Apply(Text, ("audio_channels", "6")));
        // a reset removes every line of the key, not its examples
        var reset = Apply(Text, ("volume", null), ("window_size", null));
        Assert.Equal(Text.Replace("volume = 10\n", "", StringComparison.Ordinal).Replace("volume = 80\n", "", StringComparison.Ordinal), reset);
        // an empty file, and no file at all
        Assert.Equal("[audio]\nvolume = 5\n", Apply("", ("volume", "5")));
        Assert.Equal("a = 1\n\n[audio]\nvolume = 5\naudio_channels = 2\n\n[display]\nfps = 20\n", Apply("a = 1", ("volume", "5"), ("fps", "20"), ("audio_channels", "2")));
    }

    [Fact]
    public void A_file_in_another_encoding_keeps_its_bytes()
    {
        // a path in Windows-1252 (not valid UTF-8)
        var file = Encoding.Latin1.GetBytes("romfs = C:\\Jogos\\Cora\u00E7\u00E3o.nsp\r\n[display]\r\nfullscreen = 1\r\n");
        var (target, install, ini, _) = Game();
        File.WriteAllBytes(ini, file);
        IGameTarget t = target;
        t.WriteGameSettings(install, One("fullscreen", "0"));
        var after = File.ReadAllBytes(ini);
        Assert.Equal(file.Length, after.Length);
        Assert.Equal(file.AsSpan(0, file.Length - 3).ToArray(), after.AsSpan(0, file.Length - 3).ToArray());
        Assert.Equal((byte)'0', after[^3]);
    }

    [Fact]
    public void Creates_the_file_when_the_game_has_none()
    {
        var (target, install, ini, _) = Game();
        IGameTarget t = target;
        Assert.Empty(t.ReadGameSettings(install));
        t.WriteGameSettings(install, One("volume", null)); // nothing to reset: no file made
        Assert.False(File.Exists(ini));
        t.WriteGameSettings(install, One("rumble_curve", "0.80"));
        Assert.Equal("[input]\nrumble_curve = 0.8\n", File.ReadAllText(ini));
    }

    [Fact]
    public void The_settings_are_in_the_data_folder_when_game_dir_moved_the_installation()
    {
        var (target, install, ini, _) = Game();
        var data = Rig.NewFolder("gamesettings-data");
        File.WriteAllText(ini, $"[launcher]\ngame_dir = \"{data}\"\n");
        install = target.Detect(install.Root)!;
        Assert.Equal(Path.Combine(data, "mkw.ini"), RecompSettings.PathFor(install));
        IGameTarget t = target;
        t.WriteGameSettings(install, One("volume", "40"));
        Assert.Equal("[audio]\nvolume = 40\n", File.ReadAllText(Path.Combine(data, "mkw.ini")));
        Assert.Equal($"[launcher]\ngame_dir = \"{data}\"\n", File.ReadAllText(ini)); // the selector beside mkw.exe is not touched
    }

    [Fact]
    public void A_linux_game_dir_is_on_wines_linux_drive()
    {
        // mkw.ini written by the Linux build (/home/deck/mkw), read by a launcher under Proton, where / is Z:\
        var (target, install, ini, _) = Game();
        var z = Rig.NewFolder("gamesettings-z") + Path.DirectorySeparatorChar;
        var data = Path.Combine(z, "home", "deck", "mkw data");
        Directory.CreateDirectory(data);
        File.WriteAllText(ini, "[launcher]\ngame_dir = \"/home/deck/mkw data\"\n");
        Assert.Equal(data, Recomp.DataDir(install.Root, z));
        Assert.Equal("/home/deck/mkw data", Recomp.DataDir(install.Root, null)); // on Windows: as written
        // Windows paths (what the Windows build under Proton writes) and relative ones are not touched
        File.WriteAllText(ini, "game_dir = Z:\\home\\deck\\mkw\n");
        Assert.Equal(@"Z:\home\deck\mkw", Recomp.DataDir(install.Root, z));
        File.WriteAllText(ini, "game_dir = data\n");
        Assert.Equal(Path.Combine(install.Root, "data"), Recomp.DataDir(install.Root, z));
        File.WriteAllText(ini, "game_dir = //server/share\n");
        Assert.Equal("//server/share", Recomp.DataDir(install.Root, z));

        // the settings then live there (the install as a launcher under Wine detects it)
        var there = install with { DataRoot = data };
        IGameTarget t = target;
        t.WriteGameSettings(there, One("volume", "40"));
        Assert.Equal("[audio]\nvolume = 40\n", File.ReadAllText(Path.Combine(data, "mkw.ini")));
    }

    [Fact]
    public void A_game_in_another_wine_prefix_is_seen_in_linuxs_process_list()
    {
        var proc = Rig.NewFolder("gamesettings-proc");
        void Process(string pid, string cmdline)
        {
            Directory.CreateDirectory(Path.Combine(proc, pid));
            File.WriteAllBytes(Path.Combine(proc, pid, "cmdline"), Encoding.UTF8.GetBytes(cmdline));
        }

        Assert.False(MkwRecompTarget.ProcHasGame(Path.Combine(proc, "missing")));
        Process("1", "/sbin/init\0");
        Process("2", ""); // a kernel thread
        Process("811", "/usr/bin/steam\0-gamepadui\0");
        Process("900", "Z:\\home\\deck\\launcher\\mkwmp.exe\0");
        Process("901", "/home/deck/tools/notmkw.exe\0--play\0");
        Directory.CreateDirectory(Path.Combine(proc, "902")); // ended meanwhile: no cmdline
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        File.WriteAllBytes(Path.Combine(proc, "self", "cmdline"), "mkw.exe\0"u8.ToArray()); // not a process id
        Assert.False(MkwRecompTarget.ProcHasGame(proc));

        Process("1200", "Z:\\home\\deck\\Games\\mkw-recomp\\MKW.exe\0--play\0"); // Wine's process for the game
        Assert.True(MkwRecompTarget.ProcHasGame(proc));
        Directory.Delete(Path.Combine(proc, "1200"), recursive: true);
        Assert.False(MkwRecompTarget.ProcHasGame(proc));
        Process("1300", "python3\0/home/deck/.steam/proton\0waitforexitandrun\0/home/deck/Games/mkw-recomp/mkw.exe\0--play\0"); // Proton's wrapper
        Assert.True(MkwRecompTarget.ProcHasGame(proc));
    }

    // ---- presets --------------------------------------------------------------------------

    [Fact]
    public void A_quality_preset_sets_its_rows_and_is_told_from_them()
    {
        var (target, install, ini, _) = Game(RealIni);
        IGameTarget t = target;
        var schema = t.GameSettings(install)!;
        Assert.False(t.ReadGameSettings(install).ContainsKey("quality"));
        Assert.Equal("console", schema.Effective(t.ReadGameSettings(install))["quality"]);

        t.WriteGameSettings(install, schema.Changes("quality", "high"));
        var set = t.ReadGameSettings(install);
        Assert.Equal(("high", "8", "full", "3", "4096"), (set["quality"], set["aniso"], set["tex_quality"], set["detail_distance"], set["shadow_size"]));
        Assert.EndsWith("\r\n[graphics]\r\nquality = high\r\naniso = 8\r\ntex_quality = full\r\ndetail_distance = 3\r\nshadow_size = 4096\r\n", File.ReadAllText(ini), StringComparison.Ordinal);

        // one of its rows changed: Custom, whatever the file's quality line still says
        t.WriteGameSettings(install, schema.Changes("aniso", "16"));
        Assert.Equal("custom", t.ReadGameSettings(install)["quality"]);
        t.WriteGameSettings(install, schema.Changes("detail_distance", "4"));
        Assert.Equal("ultra", t.ReadGameSettings(install)["quality"]);

        // a reset of the preset puts its rows back too
        t.WriteGameSettings(install, schema.Changes("quality", null));
        Assert.Equal("1", Assert.Single(t.ReadGameSettings(install)).Value);
        Assert.Equal("console", schema.Effective(t.ReadGameSettings(install))["quality"]);

        Assert.Throws<LauncherException>(() => schema.Changes("quality", "best"));
        Assert.Throws<LauncherException>(() => schema.Changes("no_such", "1"));
    }

    [Fact]
    public void Conditions_follow_the_values_in_effect()
    {
        var (target, install, _, _) = Game(RealIni);
        IGameTarget t = target;
        var schema = t.GameSettings(install)!;
        var values = schema.Effective(t.ReadGameSettings(install));
        Assert.False(GameSettingsSchema.IsVisible(schema.Find("window_size")!, values)); // fullscreen = 1
        Assert.False(GameSettingsSchema.IsVisible(schema.Find("sharpness")!, values)); // bilinear by default
        Assert.True(GameSettingsSchema.IsVisible(schema.Find("volume")!, values));
        t.WriteGameSettings(install, new Dictionary<string, string?> { ["fullscreen"] = "0", ["upscaler"] = "fsr" });
        values = schema.Effective(t.ReadGameSettings(install));
        Assert.True(GameSettingsSchema.IsVisible(schema.Find("window_size")!, values));
        Assert.True(GameSettingsSchema.IsVisible(schema.Find("sharpness")!, values));
    }

    [Fact]
    public void A_window_size_the_menu_does_not_list_is_still_an_option()
    {
        var (target, install, _, _) = Game("window_size = 1728x1117\n");
        IGameTarget t = target;
        var size = t.GameSettings(install)!.Find("window_size")!;
        Assert.Contains(size.Options, o => o.Value == "1728x1117" && o.Label == "1728 × 1117");
        Assert.Equal("1728x1117", size.Normalize("1728x1117"));
    }

    // ---- through the launcher -------------------------------------------------------------

    [Fact]
    public async Task The_logic_rate_is_locked_to_60_while_online_and_free_offline()
    {
        await using var rig = new Rig(new FakeSeatServer());
        var exe016 = "mkwmp test mkw.exe 0.1.16"u8.ToArray(); // a runtime that has the logic rate
        Recomp.TestExes[Mkwmp.Core.Updates.Updater.Sha256Hex(exe016)] = "0.1.16";
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), exe016);
        File.WriteAllText(Path.Combine(rig.Game, "mkw.ini"), RealIni);
        await rig.Launcher.ResetGameSettingAsync("volume"); // reads the game
        Assert.True(((ILauncher)rig.Launcher).OnlineEnabled);
        var logic = rig.Launcher.GameSettings.Schema!.Find("logic_hz");
        Assert.NotNull(logic);
        Assert.Contains("60", logic.Locked, StringComparison.Ordinal);

        var file = File.ReadAllText(Path.Combine(rig.Game, "mkw.ini"));
        await rig.Launcher.SetGameSettingAsync("logic_hz", "120");
        Assert.True(rig.Launcher.GameSettings.NoteIsError);
        Assert.Equal(file, File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")));

        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Server = "" });
        await rig.Launcher.ResetGameSettingAsync("volume");
        Assert.Null(rig.Launcher.GameSettings.Schema!.Find("logic_hz")!.Locked);
        await rig.Launcher.SetGameSettingAsync("logic_hz", "120");
        Assert.Equal("120", rig.Launcher.GameSettings.Value("logic_hz"));
    }

    [Fact]
    public void Mkw_recomps_menu_shortcut_never_takes_the_overlays_buttons()
    {
        // mkw-recomp takes minus_plus (Select+Start) or guide, and has no "off": the one the overlay leaves free
        var want = new Dictionary<Overlay.OverlayShortcut, string>
        {
            [Overlay.OverlayShortcut.GuideOrSelectStart] = "guide", // both overlap; the mod's filter always clears Guide
            [Overlay.OverlayShortcut.Guide] = "minus_plus",
            [Overlay.OverlayShortcut.SelectStart] = "guide",
            [Overlay.OverlayShortcut.SelectHold] = "guide",
            [Overlay.OverlayShortcut.StartHold] = "guide",
            [Overlay.OverlayShortcut.SticksPressed] = "guide",
            [Overlay.OverlayShortcut.BumpersStart] = "guide",
            [Overlay.OverlayShortcut.SelectLb] = "guide",
            [Overlay.OverlayShortcut.SelectRb] = "guide",
        };
        Assert.Equal(Overlay.OverlayShortcuts.All.Order(), want.Keys.Order());
        var (target, install, _, _) = Game();
        IGameTarget t = target;
        var row = t.GameSettings(install)!.Find(RecompSettings.MenuShortcut)!;
        foreach (var (shortcut, value) in want)
        {
            Assert.Equal(value, RecompSettings.MenuShortcutFor(shortcut));
            Assert.Equal([(RecompSettings.MenuShortcut, value)], t.OverlayOwnedSettings(shortcut));
            Assert.Equal(value, row.Normalize(value));
            if (shortcut == Overlay.OverlayShortcut.Guide)
            {
                Assert.NotEqual("guide", value);
            }
            if (shortcut == Overlay.OverlayShortcut.SelectStart)
            {
                Assert.NotEqual("minus_plus", value);
            }
        }
    }

    [Fact]
    public async Task Mkw_recomps_menu_shortcut_is_hidden_and_follows_the_overlay_shortcut()
    {
        await using var rig = new Rig(new FakeSeatServer());
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        var ini = Path.Combine(rig.Game, "mkw.ini");
        File.WriteAllText(ini, RealIni);
        IGameTarget game = rig.Target;
        var install = rig.Target.Detect(rig.Game)!;
        await rig.Launcher.ResetGameSettingAsync("volume"); // reads the game
        var view = rig.Launcher.GameSettings;
        // not a row of Settings › Controls, and not the player's to change or reset
        Assert.Null(view.Schema!.Find(RecompSettings.MenuShortcut));
        Assert.NotNull(view.Schema.Find("pad_layout"));
        Assert.NotNull(game.GameSettings(install)!.Find(RecompSettings.MenuShortcut)); // still the game's
        await rig.Launcher.SetGameSettingAsync(RecompSettings.MenuShortcut, "minus_plus");
        Assert.True(rig.Launcher.GameSettings.NoteIsError);
        Assert.DoesNotContain("menu_shortcut", File.ReadAllText(ini), StringComparison.Ordinal);

        // a change of the overlay shortcut is written at once while the game is closed...
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Ui = rig.Launcher.Settings.Ui with { OverlayShortcut = Overlay.OverlayShortcut.Guide } });
        Assert.Equal("minus_plus", game.ReadGameSettings(install).GetValueOrDefault(RecompSettings.MenuShortcut, "minus_plus"));
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Ui = rig.Launcher.Settings.Ui with { OverlayShortcut = Overlay.OverlayShortcut.SelectHold } });
        Assert.Equal("guide", game.ReadGameSettings(install)[RecompSettings.MenuShortcut]);
        Assert.DoesNotContain(RecompSettings.MenuShortcut, rig.Launcher.GameSettings.Changed); // nothing for "reset all"

        // ...and not while it runs (mkw-recomp reads mkw.ini only at start): the next start writes it
        var running = true;
        rig.Target.RunningOverride = () => running;
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Ui = rig.Launcher.Settings.Ui with { OverlayShortcut = Overlay.OverlayShortcut.Guide } });
        Assert.Equal("guide", game.ReadGameSettings(install)[RecompSettings.MenuShortcut]);
        running = false;

        // the player (or mkw-recomp's own menu) set it back: the start puts the launcher's value in again
        File.WriteAllText(ini, RealIni.Replace("[display]", "[input]\r\nmenu_shortcut = guide\r\n\r\n[display]", StringComparison.Ordinal));
        await rig.Launcher.PlayAsync();
        await Rig.Until("the game", () => rig.Started);
        Assert.Equal("minus_plus", game.ReadGameSettings(install).GetValueOrDefault(RecompSettings.MenuShortcut, "minus_plus"));
        Assert.Contains("romfs = ", File.ReadAllText(ini), StringComparison.Ordinal); // the rest of the file stays
    }

    [Fact]
    public async Task The_launcher_shows_and_changes_the_targets_settings()
    {
        await using var rig = new Rig(new FakeSeatServer());
        // the rig's game is a runtime without known settings
        Assert.Null(rig.Launcher.GameSettings.Schema);
        Assert.False(rig.Launcher.GameSettings.Editable);
        Assert.Contains("mkw-recomp", rig.Launcher.GameSettings.Note, StringComparison.Ordinal);
        await rig.Launcher.SetGameSettingAsync("volume", "50");
        Assert.True(rig.Launcher.GameSettings.NoteIsError);
        Assert.False(File.Exists(Path.Combine(rig.Game, "mkw.ini")));

        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        File.WriteAllText(Path.Combine(rig.Game, "mkw.ini"), RealIni);
        var changes = 0;
        rig.Launcher.Changed += (_, _) => Interlocked.Increment(ref changes);
        await rig.Launcher.ResetGameSettingAsync("volume"); // any change reads the game again
        var view = rig.Launcher.GameSettings;
        Assert.NotNull(view.Schema);
        Assert.True(view.Editable);
        Assert.Null(view.Note);
        Assert.Equal("1", view.Value("fullscreen"));
        Assert.Equal("100", view.Value("volume"));
        Assert.Equal("fullscreen", Assert.Single(view.Changed));
        Assert.False(view.IsVisible(view.Schema.Find("window_size")!));
        Assert.Same(view, rig.Launcher.GameSettings);

        await rig.Launcher.SetGameSettingAsync("volume", "35");
        await rig.Launcher.SetGameSettingAsync("quality", "low");
        view = rig.Launcher.GameSettings;
        Assert.Equal(("35", "low", "quarter", "1024"), (view.Value("volume"), view.Value("quality"), view.Value("tex_quality"), view.Value("shadow_size")));
        Assert.Contains("volume", view.Changed);
        Assert.True(changes >= 3);

        // a refusal leaves the file and the values, and says why
        var file = File.ReadAllText(Path.Combine(rig.Game, "mkw.ini"));
        await rig.Launcher.SetGameSettingAsync("volume", "500");
        view = rig.Launcher.GameSettings;
        Assert.True(view.NoteIsError);
        Assert.Contains("Volume", view.Note, StringComparison.Ordinal);
        Assert.Equal("35", view.Value("volume"));
        Assert.Equal(file, File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")));
        await rig.Launcher.SetGameSettingAsync("no_such_setting", "1");
        Assert.True(rig.Launcher.GameSettings.NoteIsError);

        await rig.Launcher.ResetGameSettingAsync("volume");
        view = rig.Launcher.GameSettings;
        Assert.Null(view.Note);
        Assert.Equal("100", view.Value("volume"));
        Assert.DoesNotContain("volume", view.Changed);

        await rig.Launcher.ResetGameSettingsAsync();
        view = rig.Launcher.GameSettings;
        Assert.Empty(view.Changed);
        Assert.Equal("0", view.Value("fullscreen"));
        Assert.Equal("mkwmp", Recomp.ModsIn(File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")))); // not a setting: kept
        Assert.Contains("romfs = ", File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_refuses_while_the_game_runs()
    {
        var running = false;
        await using var rig = new Rig(new FakeSeatServer());
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        File.WriteAllText(Path.Combine(rig.Game, "mkw.ini"), RealIni);
        rig.Target.RunningOverride = () => running;
        Assert.True(rig.Launcher.GameSettings.Editable);
        running = true;
        await rig.Launcher.SetGameSettingAsync("volume", "35");
        var view = rig.Launcher.GameSettings;
        Assert.False(view.Editable);
        Assert.True(view.NoteIsError);
        Assert.Equal(RecompSettings.GameIsRunning, view.Note);
        Assert.Equal("100", view.Value("volume"));
        Assert.Equal(RealIni, File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")));
        running = false;
        await rig.Launcher.SetGameSettingAsync("volume", "35");
        Assert.True(rig.Launcher.GameSettings.Editable);
        Assert.Equal("35", rig.Launcher.GameSettings.Value("volume"));
    }
}
